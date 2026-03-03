using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using N01D.Overwatch.Models;
using N01D.Overwatch.Services;

namespace N01D.Overwatch
{
    public partial class MainWindow : Window
    {
        private readonly RssFeedService _rss = new();
        private readonly FlightTrackingService _flights = new();
        private readonly ShipTrackingService _ships = new();
        private readonly AlertService _alerts = new();
        private readonly EclipseService _eclipse = new();
        private readonly MissileDefenseService _missiles = new();
        private readonly WarMonitoringService _warOps = new();
        private readonly EquipmentDatabaseService _equipment = new();
        private readonly RadioStreamService _radio = new();
        private readonly GroundTrackingService _ground = new();
        private readonly MarketImpactService _market = new();

        private readonly ObservableCollection<EventViewModel> _timelineItems = new();
        private readonly List<ConflictEvent> _allEvents = new();
        private DispatcherTimer? _autoRefreshTimer;
        private DispatcherTimer? _eclipseTimer;
        private DispatcherTimer? _liveTrackingTimer;
        private bool _isLoading;
        private bool _initialized;
        private bool _liveTrackingEnabled;
        private int _liveTrackingCycles;
        private DateTime _lastFlightScan = DateTime.MinValue;
        private DateTime _lastWarOpsScan = DateTime.MinValue;
        private DateTime _lastGroundScan = DateTime.MinValue;
        private List<FlightData> _lastFlights = new();

        public MainWindow()
        {
            InitializeComponent();
            lstTimeline.ItemsSource = _timelineItems;
            LoadAlertRules();
            LoadEclipseData();
            LoadMissileDefenseData();
            LoadEquipmentData();
            LoadRadioStreams();
            LoadGroundData();
            LoadMarketData();
            _initialized = true;
            Loaded += async (_, _) => await RefreshAllAsync();
            InitMap();
            StartEclipseCountdown();
        }

        // ═══════════════════════════════════════════
        //  REFRESH ALL DATA SOURCES
        // ═══════════════════════════════════════════

        private async Task RefreshAllAsync()
        {
            if (_isLoading) return;
            _isLoading = true;
            txtStatus.Text = "⟳ Scanning all feeds...";
            btnRefresh.IsEnabled = false;

            try
            {
                var tasks = new List<Task>();

                // RSS Feeds
                var rssTask = _rss.FetchAllAsync();
                tasks.Add(rssTask);

                // Ships
                var shipTask = _ships.FetchVesselsAsync();
                tasks.Add(shipTask);

                // Flights (now included in auto-refresh)
                var flightTask = _flights.FetchMilitaryFlightsAsync();
                tasks.Add(flightTask);

                // War Ops (periodic — every 5 min max)
                Task<List<ConflictEvent>>? warOpsTask = null;
                if ((DateTime.UtcNow - _lastWarOpsScan).TotalMinutes >= 5 || _lastWarOpsScan == DateTime.MinValue)
                {
                    warOpsTask = _warOps.FetchAllAsync();
                    tasks.Add(warOpsTask);
                }

                // Ground thermal hotspots — NASA FIRMS satellite data (every 10 min)
                Task<List<ThermalHotspot>>? firmsTask = null;
                if ((DateTime.UtcNow - _lastGroundScan).TotalMinutes >= 10 || _lastGroundScan == DateTime.MinValue)
                {
                    firmsTask = _ground.FetchFirmsDataAsync();
                    tasks.Add(firmsTask);
                }

                // Equipment intelligence feeds — defense news RSS (every 10 min)
                var equipIntelTask = _equipment.FetchEquipmentIntelAsync();
                tasks.Add(equipIntelTask);

                await Task.WhenAll(tasks);

                // Process RSS events
                var rssEvents = await rssTask;
                foreach (var ev in rssEvents)
                {
                    _alerts.EvaluateEvent(ev);
                    if (!_allEvents.Any(e => e.Title == ev.Title && e.Source == ev.Source))
                        _allEvents.Add(ev);
                }

                // Process ship data
                var vessels = await shipTask;
                _allEvents.RemoveAll(e => e.DataSource == DataSource.ShipTracker);
                foreach (var v in vessels)
                    _allEvents.Add(_ships.ToConflictEvent(v));
                dgVessels.ItemsSource = vessels;

                // Process flights
                var flights = await flightTask;
                _lastFlights = flights;
                _lastFlightScan = DateTime.UtcNow;
                dgFlights.ItemsSource = flights;
                txtFlightCount.Text = _flights.IsRateLimited && flights.Count > 0
                    ? $"{flights.Count} aircraft (cached — API rate-limited)"
                    : _flights.IsRateLimited
                    ? "API rate-limited — waiting for cooldown"
                    : $"{flights.Count} military aircraft detected";
                _allEvents.RemoveAll(e2 => e2.DataSource == DataSource.FlightTracker);
                foreach (var f in flights)
                {
                    var ev = _flights.ToConflictEvent(f);
                    if (ev != null)
                    {
                        _alerts.EvaluateEvent(ev);
                        _allEvents.Add(ev);
                    }
                }
                UpdateMapFlights(flights);

                // Process war ops
                if (warOpsTask != null)
                {
                    var warEvents = await warOpsTask;
                    _lastWarOpsScan = DateTime.UtcNow;
                    foreach (var ev in warEvents)
                    {
                        _alerts.EvaluateEvent(ev);
                        if (!_allEvents.Any(e2 => e2.Title == ev.Title && e2.Source == ev.Source))
                            _allEvents.Add(ev);
                    }

                    // Update war ops sub-tabs
                    var sigacts = warEvents.Where(e2 => e2.Tags.Contains("SIGACT")).Select(e2 => new EventViewModel(e2)).ToList();
                    var sanctions = warEvents.Where(e2 => e2.Tags.Contains("SANCTIONS")).Select(e2 => new EventViewModel(e2)).ToList();
                    var cyberOps = warEvents.Where(e2 => e2.Tags.Contains("CYBER")).Select(e2 => new EventViewModel(e2)).ToList();
                    var proxy = warEvents.Where(e2 => e2.Tags.Contains("PROXY")).Select(e2 => new EventViewModel(e2)).ToList();
                    lstSigact.ItemsSource = sigacts;
                    lstSanctions.ItemsSource = sanctions;
                    lstCyberOps.ItemsSource = cyberOps;
                    lstProxy.ItemsSource = proxy;
                    txtWarOpsCount.Text = $"{warEvents.Count} war actions detected";
                }

                // Process FIRMS thermal data — merge live satellite hotspots
                if (firmsTask != null)
                {
                    var firmsHotspots = await firmsTask;
                    _lastGroundScan = DateTime.UtcNow;
                    if (firmsHotspots.Count > 0)
                    {
                        _ground.MergeFirmsHotspots(firmsHotspots);
                        dgThermal.ItemsSource = _ground.GetAllHotspots();
                        UpdateMapGround();
                        var stats = _ground.GetStats();
                        txtGroundStats.Text = $"{stats.ActiveFlocks} active flocks | {stats.ConflictZones} conflict zones ({stats.HighIntensityZones} HIGH) | " +
                                              $"{stats.ThermalHotspots} thermal hotspots (LIVE) | {stats.Checkpoints} checkpoints/FOBs | " +
                                              $"{stats.IdpCorridors} IDP corridors ({stats.TotalDisplaced:N0} displaced) | {stats.Countries} countries";
                    }
                }

                // Process equipment intelligence feed
                try
                {
                    var intel = await equipIntelTask;
                    if (intel.Count > 0)
                    {
                        dgEquipmentIntel.ItemsSource = intel;
                        var newCount = intel.Count(i => i.IsNew);
                        var statsEq = _equipment.GetStats();
                        txtEquipmentStats.Text = $"{statsEq.Countries} forces | {statsEq.TotalTypes} weapon systems | {statsEq.TotalActive:N0} active units | " +
                                                 $"✈ {statsEq.Aircraft:N0} aircraft | 🛩 {statsEq.Drones:N0} drones | 🚢 {statsEq.Naval:N0} naval | 🚀 {statsEq.Missiles:N0} missiles | " +
                                                 $"📡 {intel.Count} intel items" + (newCount > 0 ? $" ({newCount} NEW)" : "");
                    }
                }
                catch { }

                ApplyFilters();
                UpdateAlertCount();
                UpdateThreatLevel();
                UpdateMap();

                txtLastUpdate.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
                var liveLabel = _liveTrackingEnabled ? " [LIVE]" : "";
                txtStatus.Text = $"✓ {_allEvents.Count} events | {flights.Count} flights | {vessels.Count} ships{liveLabel}";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"⚠ Error: {ex.Message}";
            }
            finally
            {
                _isLoading = false;
                btnRefresh.IsEnabled = true;
            }
        }

        // ═══════════════════════════════════════════
        //  FLIGHT SCANNING
        // ═══════════════════════════════════════════

        private async void BtnScanFlights_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "✈ Scanning Middle East airspace...";
            try
            {
                var flights = await _flights.FetchMilitaryFlightsAsync();
                dgFlights.ItemsSource = flights;
                var flightLabel = _flights.IsRateLimited && flights.Count > 0
                    ? $"{flights.Count} aircraft (cached — rate-limited)"
                    : _flights.IsRateLimited
                    ? "API rate-limited — waiting for cooldown"
                    : $"{flights.Count} military aircraft detected";
                txtFlightCount.Text = flightLabel;

                // Add to timeline
                _allEvents.RemoveAll(e2 => e2.DataSource == DataSource.FlightTracker);
                foreach (var f in flights)
                {
                    var ev = _flights.ToConflictEvent(f);
                    if (ev != null)
                    {
                        _alerts.EvaluateEvent(ev);
                        _allEvents.Add(ev);
                    }
                }

                ApplyFilters();
                UpdateMap();
                UpdateMapFlights(flights);
                txtStatus.Text = $"✈ {flights.Count} military flights tracked";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"⚠ Flight scan error: {ex.Message}";
            }
        }

        private async void BtnScanShips_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "🚢 Scanning maritime zones...";
            try
            {
                var vessels = await _ships.FetchVesselsAsync();
                dgVessels.ItemsSource = vessels;
                txtStatus.Text = $"🚢 {vessels.Count} maritime contacts";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"⚠ Maritime scan error: {ex.Message}";
            }
        }

        // ═══════════════════════════════════════════
        //  WAR OPS MONITORING
        // ═══════════════════════════════════════════

        private async void BtnScanWarOps_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "⚔ Scanning war operations feeds...";
            try
            {
                var events = await _warOps.FetchAllAsync();

                // Classify and add to timeline
                foreach (var ev in events)
                {
                    _alerts.EvaluateEvent(ev);
                    if (!_allEvents.Any(e2 => e2.Title == ev.Title && e2.Source == ev.Source))
                        _allEvents.Add(ev);
                }

                // Populate sub-tabs
                var sigacts = events.Where(e2 => e2.Tags.Contains("SIGACT")).Select(e2 => new EventViewModel(e2)).ToList();
                var sanctions = events.Where(e2 => e2.Tags.Contains("SANCTIONS")).Select(e2 => new EventViewModel(e2)).ToList();
                var cyberOps = events.Where(e2 => e2.Tags.Contains("CYBER")).Select(e2 => new EventViewModel(e2)).ToList();
                var proxy = events.Where(e2 => e2.Tags.Contains("PROXY")).Select(e2 => new EventViewModel(e2)).ToList();

                lstSigact.ItemsSource = sigacts;
                lstSanctions.ItemsSource = sanctions;
                lstCyberOps.ItemsSource = cyberOps;
                lstProxy.ItemsSource = proxy;

                var total = events.Count;
                txtWarOpsCount.Text = $"{total} war actions detected";

                ApplyFilters();
                UpdateMap();
                txtStatus.Text = $"⚔ {total} war actions tracked ({sigacts.Count} SIGACT, {sanctions.Count} sanctions, {cyberOps.Count} cyber, {proxy.Count} proxy)";
            }
            catch (Exception ex)
            {
                txtStatus.Text = $"⚠ War ops scan error: {ex.Message}";
            }
        }

        // ═══════════════════════════════════════════
        //  FILTERING & TIMELINE
        // ═══════════════════════════════════════════

        private void ApplyFilters()
        {
            if (!_initialized) return;
            var search = txtSearch.Text?.Trim().ToLowerInvariant() ?? "";
            var enabledCategories = new HashSet<EventCategory>();
            if (chkMilitary.IsChecked == true) enabledCategories.Add(EventCategory.Military);
            if (chkDiplomatic.IsChecked == true) enabledCategories.Add(EventCategory.Diplomatic);
            if (chkEconomic.IsChecked == true) enabledCategories.Add(EventCategory.Economic);
            if (chkNuclear.IsChecked == true) enabledCategories.Add(EventCategory.Nuclear);
            if (chkCyber.IsChecked == true) enabledCategories.Add(EventCategory.Cyber);
            if (chkHumanitarian.IsChecked == true) enabledCategories.Add(EventCategory.Humanitarian);
            if (chkIntelligence.IsChecked == true) enabledCategories.Add(EventCategory.Intelligence);

            var filtered = _allEvents
                .Where(e => enabledCategories.Contains(e.Category))
                .Where(e => string.IsNullOrEmpty(search) ||
                            e.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            e.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            e.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            e.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => e.IsAlert)
                .ThenByDescending(e => e.Severity)
                .ThenByDescending(e => e.Timestamp)
                .Take(500)
                .ToList();

            _timelineItems.Clear();
            foreach (var e in filtered)
                _timelineItems.Add(new EventViewModel(e));
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilters();

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilters();

        // ═══════════════════════════════════════════
        //  EVENT DETAIL
        // ═══════════════════════════════════════════

        private void LstTimeline_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstTimeline.SelectedItem is EventViewModel vm)
            {
                txtDetailTitle.Text = vm.Title;
                txtDetailSeverity.Text = $"Severity: {vm.SeverityLabel}";
                txtDetailSeverity.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(vm.SeverityColor)!);
                txtDetailCategory.Text = $"Category: {vm.Event.Category}";
                txtDetailSource.Text = $"Source: {vm.Source}";
                txtDetailTime.Text = $"Time: {vm.Event.Timestamp:yyyy-MM-dd HH:mm:ss UTC}";
                txtDetailLocation.Text = string.IsNullOrEmpty(vm.Event.Location) ? "" : $"Location: {vm.Event.Location}";
                txtDetailSummary.Text = vm.Summary;
                txtDetailTags.Text = vm.TagsDisplay;
                btnOpenUrl.Visibility = string.IsNullOrEmpty(vm.Event.SourceUrl) ? Visibility.Collapsed : Visibility.Visible;
                btnOpenUrl.Tag = vm.Event.SourceUrl;

                // Fly map to event location if it has coordinates
                if (vm.Event.Latitude.HasValue && vm.Event.Longitude.HasValue)
                {
                    try
                    {
                        webMap.CoreWebView2?.ExecuteScriptAsync(
                            $"flyTo({vm.Event.Latitude},{vm.Event.Longitude},8)");
                    }
                    catch { }
                }
            }
        }

        private void BtnOpenUrl_Click(object sender, RoutedEventArgs e)
        {
            if (btnOpenUrl.Tag is string url && !string.IsNullOrEmpty(url))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            }
        }

        // ═══════════════════════════════════════════
        //  ALERTS
        // ═══════════════════════════════════════════

        private void LoadAlertRules()
        {
            var vms = _alerts.Rules.Select(r => new AlertRuleViewModel(r)).ToList();
            lstAlerts.ItemsSource = vms;
        }

        private void UpdateAlertCount()
        {
            var count = _allEvents.Count(e => e.IsAlert);
            txtAlertCount.Text = $"⚠ {count} ALERT{(count != 1 ? "S" : "")}";
        }

        private void UpdateThreatLevel()
        {
            var criticals = _allEvents.Count(e => e.Severity == SeverityLevel.Critical);
            var highs = _allEvents.Count(e => e.Severity == SeverityLevel.High);

            string level;
            string color;
            if (criticals > 0) { level = "CRITICAL"; color = "#EE3333"; }
            else if (highs > 5) { level = "HIGH"; color = "#EE3333"; }
            else if (highs > 0) { level = "ELEVATED"; color = "#FF8833"; }
            else { level = "GUARDED"; color = "#33CC33"; }

            txtThreatLevel.Text = $"THREAT LEVEL: {level}";
            txtThreatLevel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        }

        // ═══════════════════════════════════════════
        //  EXPORT
        // ═══════════════════════════════════════════

        private void BtnExportJson_Click(object sender, RoutedEventArgs e)
        {
            _alerts.ExportArchive(_allEvents, "json");
            txtStatus.Text = $"📥 Exported {_allEvents.Count} events to JSON — {_alerts.GetArchivePath()}";
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            _alerts.ExportArchive(_allEvents, "csv");
            txtStatus.Text = $"📥 Exported {_allEvents.Count} events to CSV — {_alerts.GetArchivePath()}";
        }

        // ═══════════════════════════════════════════
        //  MAP (WebView2 — Leaflet.js)
        // ═══════════════════════════════════════════

        private async void InitMap()
        {
            try
            {
                await webMap.EnsureCoreWebView2Async();
                webMap.CoreWebView2.NavigateToString(BuildMapHtml());

                // Handle messages from map JavaScript (event clicks, URL opens)
                webMap.CoreWebView2.WebMessageReceived += (_, args) =>
                {
                    try
                    {
                        var json = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson.Trim('"').Replace("\\\"", "\"").Replace("\\\\", "\\"));
                        var root = json.RootElement;
                        var msgType = root.GetProperty("type").GetString();

                        if (msgType == "selectEvent")
                        {
                            var id = root.GetProperty("id").GetString();
                            var vm = _timelineItems.FirstOrDefault(t => t.Event.Id == id);
                            if (vm != null)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    tabMain.SelectedIndex = 0; // Switch to Timeline tab
                                    lstTimeline.SelectedItem = vm;
                                    lstTimeline.ScrollIntoView(vm);
                                });
                            }
                        }
                        else if (msgType == "openUrl")
                        {
                            var url = root.GetProperty("url").GetString();
                            if (!string.IsNullOrEmpty(url))
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        }
                        else if (msgType == "selectFlight")
                        {
                            Dispatcher.Invoke(() => tabMain.SelectedIndex = 2); // Switch to Flights tab
                        }
                    }
                    catch { }
                };
            }
            catch { }
        }

        private void UpdateMap()
        {
            try
            {
                // ─── Event markers with rich data ───
                var geoEvents = _allEvents.Where(e => e.Latitude.HasValue && e.Longitude.HasValue).ToList();
                var markers = string.Join(",", geoEvents.Select(e =>
                {
                    var color = e.Severity switch
                    {
                        SeverityLevel.Critical => "#EE3333",
                        SeverityLevel.High => "#FF8833",
                        SeverityLevel.Medium => "#3388FF",
                        _ => "#6A6A80"
                    };
                    var sev = (int)e.Severity;
                    var icon = e.DataSource switch
                    {
                        DataSource.FlightTracker => "✈",
                        DataSource.ShipTracker => "⚓",
                        DataSource.OilPrice => "🛢",
                        _ => e.Category switch
                        {
                            EventCategory.Military => "⚔",
                            EventCategory.Nuclear => "☢",
                            EventCategory.Cyber => "💻",
                            EventCategory.Diplomatic => "🏛",
                            _ => "●"
                        }
                    };
                    var catLabel = e.Category.ToString().ToUpperInvariant();
                    var title = Escape(e.Title);
                    var summary = Escape(e.Summary.Length > 200 ? e.Summary[..200] + "..." : e.Summary);
                    var source = Escape(e.Source);
                    var url = Escape(e.SourceUrl);
                    var time = e.Timestamp.ToString("yyyy-MM-dd HH:mm");
                    var tags = e.Tags.Count > 0 ? Escape(string.Join(" ", e.Tags.Select(t => $"[{t}]"))) : "";

                    return $"{{lat:{e.Latitude},lon:{e.Longitude},title:'{title}',color:'{color}',sev:{sev}," +
                           $"icon:'{icon}',cat:'{catLabel}',summary:'{summary}',source:'{source}'," +
                           $"url:'{url}',time:'{time}',tags:'{tags}',id:'{e.Id}'}}";
                }));

                webMap.CoreWebView2?.ExecuteScriptAsync($"updateMarkers([{markers}])");

                // ─── Heatmap density circles ───
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateHeatmap([{markers}])");

                // ─── Missile sites, defense systems, eclipse paths ───
                UpdateMapMissiles();
                UpdateMapDefense();
                UpdateMapEclipses();
                UpdateMapGround();
            }
            catch { }
        }

        /// <summary>Updates flight markers on the map with heading-rotated aircraft icons.</summary>
        private void UpdateMapFlights(List<FlightData> flights)
        {
            try
            {
                var flightMarkers = string.Join(",", flights.Select(f =>
                {
                    var sev = FlightTrackingService.ClassifyFlightSeverity(f);
                    return $"{{lat:{f.Latitude},lon:{f.Longitude},callsign:'{Escape(f.Callsign)}'," +
                           $"type:'{Escape(f.AircraftType)}',country:'{Escape(f.Country)}'," +
                           $"alt:{f.Altitude},spd:{f.Speed},hdg:{f.Heading}," +
                           $"icao:'{Escape(f.Registration)}',sev:{(int)sev}}}";
                }));
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateFlights([{flightMarkers}])");
            }
            catch { }
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

        private static string BuildMapHtml()
        {
            return """
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8"/>
                <style>
                    * { box-sizing: border-box; }
                    body { margin: 0; background: #080810; font-family: Consolas, monospace; }
                    #map { width: 100vw; height: 100vh; }

                    /* XChat-themed popup */
                    .event-popup { min-width: 280px; max-width: 380px; }
                    .event-popup .popup-header {
                        display: flex; align-items: center; gap: 8px;
                        padding: 8px 10px; border-radius: 4px 4px 0 0;
                        font-weight: bold; font-size: 13px;
                    }
                    .event-popup .popup-body {
                        padding: 8px 10px; font-size: 11px; color: #D0D0D0;
                        background: #10101C; border-radius: 0 0 4px 4px;
                        line-height: 1.6;
                    }
                    .event-popup .popup-body .meta { color: #6A6A80; font-size: 10px; margin-top: 6px; }
                    .event-popup .popup-body .tags { color: #AA55FF; font-size: 10px; margin-top: 3px; }
                    .event-popup .popup-body .open-link {
                        display: inline-block; margin-top: 8px; padding: 3px 10px;
                        background: #161625; color: #3388FF; border: 1px solid #2A2A3E;
                        border-radius: 3px; cursor: pointer; font-size: 10px;
                        text-decoration: none; font-family: Consolas, monospace;
                    }
                    .event-popup .popup-body .open-link:hover { background: #1E1E35; border-color: #3388FF; }

                    /* Severity badge */
                    .sev-badge {
                        padding: 1px 6px; border-radius: 3px; font-size: 9px;
                        font-weight: bold; letter-spacing: 0.5px;
                    }
                    .sev-critical { background: #EE333344; color: #EE3333; border: 1px solid #EE333366; }
                    .sev-high { background: #FF883344; color: #FF8833; border: 1px solid #FF883366; }
                    .sev-medium { background: #3388FF44; color: #3388FF; border: 1px solid #3388FF66; }
                    .sev-low { background: #6A6A8044; color: #6A6A80; border: 1px solid #6A6A8066; }

                    /* Legend panel */
                    .legend {
                        position: absolute; bottom: 12px; left: 12px; z-index: 1000;
                        background: #080810ee; border: 1px solid #2A2A3E; border-radius: 4px;
                        padding: 10px 14px; font-size: 10px; color: #D0D0D0;
                        backdrop-filter: blur(8px); max-width: 200px;
                    }
                    .legend h4 { margin: 0 0 8px 0; color: #3388FF; font-size: 11px; }
                    .legend-item { display: flex; align-items: center; gap: 6px; margin: 4px 0; }
                    .legend-dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
                    .legend-line { width: 16px; height: 0; border-top: 2px dashed; display: inline-block; }

                    /* Stats overlay */
                    .stats-panel {
                        position: absolute; top: 12px; right: 12px; z-index: 1000;
                        background: #080810ee; border: 1px solid #2A2A3E; border-radius: 4px;
                        padding: 10px 14px; font-size: 10px; color: #D0D0D0;
                        backdrop-filter: blur(8px); min-width: 190px;
                    }
                    .stats-panel h4 { margin: 0 0 6px 0; color: #AA55FF; font-size: 11px; }
                    .stat-row { display: flex; justify-content: space-between; margin: 3px 0; }
                    .stat-val { font-weight: bold; }

                    /* Layer control — collapsible, compact */
                    .layer-control {
                        position: absolute; top: 12px; left: 12px; z-index: 1000;
                        background: #080810ee; border: 1px solid #2A2A3E; border-radius: 4px;
                        padding: 6px 10px; font-size: 9px; color: #D0D0D0;
                        backdrop-filter: blur(8px); max-height: calc(100vh - 40px); overflow-y: auto;
                        transition: all 0.2s ease;
                    }
                    .layer-control.collapsed { padding: 6px 10px; }
                    .layer-control.collapsed .layer-list { display: none; }
                    .layer-control h4 { margin: 0 0 4px 0; color: #33CCCC; font-size: 10px; cursor: pointer; user-select: none; }
                    .layer-control h4:hover { color: #3388FF; }
                    .layer-toggle { display: flex; align-items: center; gap: 4px; margin: 1px 0; cursor: pointer; white-space: nowrap; }
                    .layer-toggle:hover { color: #3388FF; }
                    .layer-toggle input { accent-color: #3388FF; width: 12px; height: 12px; margin: 0; }

                    /* Pulse animation for critical markers */
                    @keyframes pulse-ring {
                        0% { r: 6; opacity: 1; }
                        100% { r: 20; opacity: 0; }
                    }
                    @keyframes pulse {
                        0% { opacity: 1; }
                        50% { opacity: 0.3; }
                        100% { opacity: 1; }
                    }

                    /* Custom Leaflet overrides */
                    .leaflet-popup-content-wrapper { background: #161625 !important; border: 1px solid #2A2A3E; border-radius: 4px !important; padding: 0 !important; box-shadow: 0 4px 20px rgba(0,0,0,0.6) !important; }
                    .leaflet-popup-content { margin: 0 !important; }
                    .leaflet-popup-tip { background: #161625 !important; border: 1px solid #2A2A3E; }
                    .leaflet-control-zoom a { background: #161625 !important; color: #3388FF !important; border-color: #2A2A3E !important; }
                    .leaflet-control-zoom a:hover { background: #1E1E35 !important; }
                    .leaflet-control-attribution { background: #080810cc !important; color: #6A6A80 !important; font-size: 9px !important; }
                    .leaflet-control-attribution a { color: #3388FF !important; }
                </style>
                <link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css"/>
                <script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>
            </head>
            <body>
                <div id="map"></div>

                <!-- Layer Control -->
                <div class="layer-control" id="layerControl">
                    <h4 onclick="document.getElementById('layerList').style.display=document.getElementById('layerList').style.display==='none'?'block':'none'">⚙ LAYERS ▾</h4>
                    <div id="layerList" class="layer-list">
                        <label class="layer-toggle"><input type="checkbox" id="togEvents" checked onchange="toggleLayer('events')"> 📡 Events</label>
                        <label class="layer-toggle"><input type="checkbox" id="togFlights" checked onchange="toggleLayer('flights')"> ✈ Flights</label>
                        <label class="layer-toggle"><input type="checkbox" id="togZones" checked onchange="toggleLayer('zones')"> 🎯 Zones</label>
                        <label class="layer-toggle"><input type="checkbox" id="togBases" checked onchange="toggleLayer('bases')"> 🏴 Bases</label>
                        <label class="layer-toggle"><input type="checkbox" id="togPipelines" checked onchange="toggleLayer('pipelines')"> 🛢 Oil</label>
                        <label class="layer-toggle"><input type="checkbox" id="togMissiles" checked onchange="toggleLayer('missiles')"> 🚀 Missiles</label>
                        <label class="layer-toggle"><input type="checkbox" id="togDefense" checked onchange="toggleLayer('defense')"> 🛡 ADS</label>
                        <label class="layer-toggle"><input type="checkbox" id="togEquipment" onchange="toggleLayer('equipment')"> ⚙ Equipment</label>
                        <label class="layer-toggle"><input type="checkbox" id="togGround" checked onchange="toggleLayer('ground')"> 🪖 Ground</label>
                        <label class="layer-toggle"><input type="checkbox" id="togHotspots" checked onchange="toggleLayer('hotspots')"> 🔥 Thermal</label>
                        <label class="layer-toggle"><input type="checkbox" id="togCheckpoints" checked onchange="toggleLayer('checkpoints')"> 🛑 CKP</label>
                        <label class="layer-toggle"><input type="checkbox" id="togConflict" checked onchange="toggleLayer('conflict')"> 💥 Conflict</label>
                        <label class="layer-toggle"><input type="checkbox" id="togCorridors" onchange="toggleLayer('corridors')"> 🏕 IDP</label>
                        <label class="layer-toggle"><input type="checkbox" id="togMarket" onchange="toggleLayer('market')"> 📈 Market</label>
                        <label class="layer-toggle"><input type="checkbox" id="togEclipse" onchange="toggleLayer('eclipse')"> 🌑 Eclipse</label>
                        <label class="layer-toggle"><input type="checkbox" id="togHeatmap" onchange="toggleLayer('heatmap')"> 🔥 Density</label>
                    </div>
                </div>

                <!-- Live Status -->
                <div id="liveIndicator" style="position:absolute;top:12px;left:50%;transform:translateX(-50%);z-index:1000;display:none;
                     background:#080810ee;border:1px solid #33CC33;border-radius:4px;padding:6px 16px;font-family:Consolas;font-size:12px;color:#33CC33">
                    <span style="animation:pulse 1s infinite">●</span> LIVE — <span id="liveFlightCount">0</span> aircraft — Scan #<span id="liveCycleCount">0</span>
                </div>

                <!-- Stats -->
                <div class="stats-panel" id="statsPanel">
                    <h4>📊 MAP INTEL</h4>
                    <div class="stat-row"><span>Events plotted:</span><span class="stat-val" id="statEvents">0</span></div>
                    <div class="stat-row"><span>Aircraft tracked:</span><span class="stat-val" id="statFlights">0</span></div>
                    <div class="stat-row"><span>Equipment assets:</span><span class="stat-val" id="statEquipment" style="color:#FF8833">0</span></div>
                    <div class="stat-row"><span>Ground flocks:</span><span class="stat-val" id="statFlocks" style="color:#33CC33">0</span></div>
                    <div class="stat-row"><span>Critical zones:</span><span class="stat-val" id="statZones" style="color:#EE3333">5</span></div>
                    <div class="stat-row"><span>Conflict zones:</span><span class="stat-val" id="statConflict" style="color:#EE3333">0</span></div>
                    <div class="stat-row"><span>Thermal:</span><span class="stat-val" id="statHotspots" style="color:#FF5555">0</span></div>
                    <div class="stat-row"><span>Missile sites:</span><span class="stat-val" id="statMissiles" style="color:#FF8833">0</span></div>
                    <div class="stat-row"><span>Defense:</span><span class="stat-val" id="statDefense" style="color:#33CCCC">0</span></div>
                    <div class="stat-row"><span>Market zones:</span><span class="stat-val" id="statMarket" style="color:#33CC33">0</span></div>
                    <div class="stat-row"><span>Coverage:</span><span class="stat-val">3.2M km²</span></div>
                </div>

                <!-- Legend -->
                <div class="legend">
                    <h4>SEVERITY</h4>
                    <div class="legend-item"><span class="legend-dot" style="background:#EE3333"></span> Critical</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#FF8833"></span> High</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#3388FF"></span> Medium</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#6A6A80"></span> Low</div>
                    <h4 style="margin-top:10px">MARKERS</h4>
                    <div class="legend-item"><span class="legend-dot" style="background:#AA55FF"></span> Nuclear Site</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#33CC33"></span> Military Base</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#FF8833"></span> ⚙ Equipment</div>
                    <div class="legend-item"><span class="legend-line" style="border-color:#DDCC33"></span> Oil Route</div>
                    <div class="legend-item"><span class="legend-line" style="border-color:#EE3333"></span> Chokepoint</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#FF5555"></span> 🚀 Missile Site</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#33CCCC;border:2px solid #33CCCC;width:6px;height:6px"></span> 🛡 Air Defense</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#33CC33"></span> 🪖 Ground Flock</div>
                    <div class="legend-item"><span class="legend-dot" style="background:#FF5555"></span> 🔥 Thermal Hotspot</div>
                    <div class="legend-item"><span class="legend-line" style="border-color:#FF333388;border-top-style:solid"></span> Conflict Zone</div>
                    <div class="legend-item"><span class="legend-line" style="border-color:#AA55FF;border-top-style:dashed"></span> IDP Corridor</div>
                    <div class="legend-item"><span class="legend-line" style="border-color:#DDCC33;border-top-style:dotted"></span> Eclipse Path</div>
                </div>

                <script>
                    // ═══════════════════════════════════════
                    //  MAP INITIALIZATION
                    // ═══════════════════════════════════════

                    var map = L.map('map', {
                        zoomControl: true,
                        minZoom: 3,
                        maxZoom: 18,
                        zoomSnap: 0.5,
                        wheelPxPerZoomLevel: 120
                    }).setView([28, 50], 5);

                    L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
                        attribution: '&copy; CARTO | N01D.Overwatch',
                        maxZoom: 18,
                        subdomains: 'abcd'
                    }).addTo(map);

                    // ═══════════════════════════════════════
                    //  LAYER GROUPS
                    // ═══════════════════════════════════════

                    var eventLayer = L.layerGroup().addTo(map);
                    var flightLayer = L.layerGroup().addTo(map);
                    var zoneLayer = L.layerGroup().addTo(map);
                    var baseLayer = L.layerGroup().addTo(map);
                    var pipelineLayer = L.layerGroup().addTo(map);
                    var missileLayer = L.layerGroup().addTo(map);
                    var defenseLayer = L.layerGroup().addTo(map);
                    var eclipseLayer = L.layerGroup();
                    var heatmapLayer = L.layerGroup();
                    var equipmentLayer = L.layerGroup();
                    var groundLayer = L.layerGroup().addTo(map);
                    var hotspotLayer = L.layerGroup().addTo(map);
                    var checkpointLayer = L.layerGroup().addTo(map);
                    var conflictLayer = L.layerGroup().addTo(map);
                    var corridorLayer = L.layerGroup();
                    var marketLayer = L.layerGroup();

                    var layers = {
                        events: eventLayer,
                        flights: flightLayer,
                        zones: zoneLayer,
                        bases: baseLayer,
                        pipelines: pipelineLayer,
                        missiles: missileLayer,
                        defense: defenseLayer,
                        eclipse: eclipseLayer,
                        heatmap: heatmapLayer,
                        equipment: equipmentLayer,
                        ground: groundLayer,
                        hotspots: hotspotLayer,
                        checkpoints: checkpointLayer,
                        conflict: conflictLayer,
                        corridors: corridorLayer,
                        market: marketLayer
                    };

                    function toggleLayer(name) {
                        var layer = layers[name];
                        if (!layer) return;
                        if (map.hasLayer(layer)) { map.removeLayer(layer); }
                        else { map.addLayer(layer); }
                    }

                    // ═══════════════════════════════════════
                    //  STRATEGIC ZONES (Chokepoints + Nuclear)
                    // ═══════════════════════════════════════

                    // Strait of Hormuz — world's most critical oil chokepoint
                    L.circle([26.56, 56.25], {
                        radius: 55000, color: '#EE3333', fillColor: '#EE3333',
                        fillOpacity: 0.06, weight: 2, dashArray: '8,6'
                    }).bindPopup(buildZonePopup('⛽ Strait of Hormuz', '#EE3333',
                        'World\'s most critical oil chokepoint. ~21M barrels/day (~20% of global oil) transit here.',
                        ['21M bbl/day throughput', 'Width: 54km at narrowest', 'Iranian coast to north', 'UAE/Oman coast to south',
                         'IRGCN fast-attack boat patrols', 'US 5th Fleet patrol zone']
                    )).addTo(zoneLayer);

                    // Bab el-Mandeb — Red Sea gateway
                    L.circle([12.58, 43.33], {
                        radius: 55000, color: '#FF8833', fillColor: '#FF8833',
                        fillOpacity: 0.06, weight: 2, dashArray: '8,6'
                    }).bindPopup(buildZonePopup('🚢 Bab el-Mandeb Strait', '#FF8833',
                        'Gateway between Red Sea and Gulf of Aden. Heavily threatened by Houthi anti-ship missiles.',
                        ['Width: 26km', '~6M bbl/day oil transit', 'Houthi drone/missile threat zone',
                         'Yemen to east, Djibouti/Eritrea to west', 'US/UK naval escorts active']
                    )).addTo(zoneLayer);

                    // Suez Canal
                    L.circle([30.45, 32.35], {
                        radius: 30000, color: '#FF8833', fillColor: '#FF8833',
                        fillOpacity: 0.06, weight: 2, dashArray: '8,6'
                    }).bindPopup(buildZonePopup('🏗 Suez Canal', '#FF8833',
                        'Connects Mediterranean to Red Sea. ~12% of global trade. Diversions during Houthi campaign.',
                        ['193km length', '~1M bbl/day oil transit', '~50 ships/day', 'Egypt-controlled',
                         'Revenue: ~$9B/year for Egypt']
                    )).addTo(zoneLayer);

                    // Nuclear facilities
                    var nuclearSites = [
                        { lat: 32.08, lon: 51.68, name: 'Isfahan UCF', desc: 'Uranium Conversion Facility. Converts yellowcake to UF6 for enrichment.', r: 60000 },
                        { lat: 33.72, lon: 51.73, name: 'Natanz FEP', desc: 'Primary uranium enrichment facility. Advanced centrifuge cascades. Previously sabotaged (Stuxnet).', r: 50000 },
                        { lat: 34.38, lon: 50.97, name: 'Fordow FFEP', desc: 'Fortified enrichment plant buried inside mountain. Built to withstand bunker-buster strikes.', r: 35000 },
                        { lat: 28.22, lon: 54.33, name: 'Bushehr NPP', desc: 'Iran\'s only operational nuclear power plant. Russian-built light water reactor.', r: 40000 },
                        { lat: 34.37, lon: 49.24, name: 'Arak IR-40', desc: 'Heavy water research reactor. Redesigned under JCPOA. Potential plutonium production.', r: 35000 }
                    ];
                    nuclearSites.forEach(function(s) {
                        L.circle([s.lat, s.lon], {
                            radius: s.r, color: '#AA55FF', fillColor: '#AA55FF',
                            fillOpacity: 0.04, weight: 1.5, dashArray: '5,5'
                        }).bindPopup(buildZonePopup('☢ ' + s.name, '#AA55FF', s.desc, [
                            'Lat: ' + s.lat.toFixed(3), 'Lon: ' + s.lon.toFixed(3),
                            'IAEA monitored facility', 'Click zone for details'
                        ])).addTo(zoneLayer);
                    });

                    // ═══════════════════════════════════════
                    //  MILITARY BASES & INSTALLATIONS
                    // ═══════════════════════════════════════

                    var milBases = [
                        // US Bases
                        { lat: 26.27, lon: 50.62, name: 'NSA Bahrain (US 5th Fleet HQ)', flag: '🇺🇸', type: 'Naval', desc: 'US Naval Support Activity. HQ of US 5th Fleet & Combined Maritime Forces.' },
                        { lat: 25.12, lon: 56.33, name: 'Al Dhafra Air Base', flag: '🇺🇸', type: 'Air', desc: 'Major USAF base in UAE. F-35, F-22, KC-10, RQ-4 deployments.' },
                        { lat: 29.96, lon: 47.79, name: 'Ali Al Salem Air Base', flag: '🇺🇸', type: 'Air', desc: 'US/Kuwait base. Staging hub for Central Command operations.' },
                        { lat: 25.42, lon: 51.44, name: 'Al Udeid Air Base', flag: '🇺🇸', type: 'Air', desc: 'Largest US base in ME. CENTCOM Forward HQ. 10,000+ personnel.' },
                        { lat: 11.55, lon: 43.15, name: 'Camp Lemonnier', flag: '🇺🇸', type: 'Joint', desc: 'Only permanent US base in Africa. CJTF-HOA. Drone ops over Yemen/Somalia.' },
                        // Regional bases
                        { lat: 27.85, lon: 52.35, name: 'Bandar Abbas Naval', flag: '🇮🇷', type: 'Naval', desc: 'IRIN & IRGCN headquarters. Controls Hormuz from Iranian side. Fast-attack craft, submarines.' },
                        { lat: 36.23, lon: 59.64, name: 'Mashhad Air Base', flag: '🇮🇷', type: 'Air', desc: 'IRIAF base. Su-24 Fencer strike aircraft, F-14 Tomcat interceptors.' },
                        { lat: 32.40, lon: 53.09, name: 'Esfahan AFB (8th TAB)', flag: '🇮🇷', type: 'Air', desc: 'IRIAF tactical air base. F-4 Phantom II, Su-22 Fitter. Near nuclear complex.' },
                        { lat: 30.84, lon: 34.67, name: 'Nevatim Air Base', flag: '🇮🇱', type: 'Air', desc: 'IAF base. F-35I Adir stealth fighters. Long-range strike capability vs Iran.' },
                        { lat: 31.29, lon: 34.28, name: 'Hatzerim Air Base', flag: '🇮🇱', type: 'Air', desc: 'IAF flight school & operational base. F-16I Sufa, AH-64 Apache.' },
                        { lat: 21.66, lon: 39.17, name: 'King Abdulaziz Naval Base', flag: '🇸🇦', type: 'Naval', desc: 'RSNF Western Fleet HQ. Frigate & corvette operations in Red Sea.' },
                        { lat: 37.09, lon: 37.00, name: 'Incirlik Air Base', flag: '🇹🇷', type: 'Air', desc: 'NATO / USAF base in Turkey. KC-135 tankers. B61 nuclear weapons storage.' }
                    ];
                    milBases.forEach(function(b) {
                        var icon = L.divIcon({
                            className: 'base-marker',
                            html: '<div style="width:14px;height:14px;background:#33CC3388;border:2px solid #33CC33;border-radius:2px;transform:rotate(45deg);box-shadow:0 0 6px #33CC3344"></div>',
                            iconSize: [14, 14], iconAnchor: [7, 7]
                        });
                        L.marker([b.lat, b.lon], { icon: icon })
                         .bindPopup(buildBasePopup(b))
                         .addTo(baseLayer);
                    });

                    // ═══════════════════════════════════════
                    //  OIL TRANSIT ROUTES
                    // ═══════════════════════════════════════

                    // Persian Gulf → Hormuz → Indian Ocean
                    L.polyline([
                        [29.5, 48.5], [28.0, 50.5], [26.6, 56.0], [25.0, 58.0], [22.0, 60.0], [18.0, 62.0]
                    ], { color: '#DDCC33', weight: 2, dashArray: '10,6', opacity: 0.5 })
                     .bindPopup(buildZonePopup('🛢 Persian Gulf Oil Route', '#DDCC33',
                         'Primary oil export corridor from Gulf producers through Strait of Hormuz.',
                         ['~21M bbl/day', 'Saudi + UAE + Kuwait + Iraq + Iran exports']))
                     .addTo(pipelineLayer);

                    // Red Sea → Suez route
                    L.polyline([
                        [12.5, 43.5], [15.0, 42.0], [20.0, 38.5], [25.0, 35.0], [30.0, 32.8], [31.5, 32.3]
                    ], { color: '#DDCC33', weight: 2, dashArray: '10,6', opacity: 0.5 })
                     .bindPopup(buildZonePopup('🛢 Red Sea → Suez Route', '#DDCC33',
                         'Oil and LNG transit via Red Sea through Suez Canal to Mediterranean.',
                         ['~4M bbl/day', 'Subject to Houthi interdiction']))
                     .addTo(pipelineLayer);

                    // East-West pipeline (Saudi Arabia)
                    L.polyline([
                        [26.2, 49.5], [26.0, 44.0], [28.5, 36.5]
                    ], { color: '#FF8833', weight: 2, dashArray: '6,4', opacity: 0.4 })
                     .bindPopup(buildZonePopup('🛢 East-West Pipeline (Petroline)', '#FF8833',
                         'Saudi Aramco 1200km pipeline. Bypasses Hormuz. Capacity: 5M bbl/day.',
                         ['Abqaiq → Yanbu', 'Strategic Hormuz bypass', 'Previously attacked by Houthis']))
                     .addTo(pipelineLayer);

                    // ═══════════════════════════════════════
                    //  MARKER & POPUP BUILDERS
                    // ═══════════════════════════════════════

                    function buildZonePopup(title, color, desc, facts) {
                        var factsHtml = facts.map(function(f) { return '<div style="color:#6A6A80;font-size:10px;padding:1px 0">› ' + f + '</div>'; }).join('');
                        return '<div class="event-popup">' +
                            '<div class="popup-header" style="background:' + color + '22;color:' + color + ';border-bottom:1px solid ' + color + '44">' + title + '</div>' +
                            '<div class="popup-body">' + desc + '<div style="margin-top:8px">' + factsHtml + '</div></div></div>';
                    }

                    function buildBasePopup(b) {
                        return '<div class="event-popup">' +
                            '<div class="popup-header" style="background:#33CC3322;color:#33CC33;border-bottom:1px solid #33CC3344">' +
                            b.flag + ' ' + b.name + '</div>' +
                            '<div class="popup-body">' +
                            '<div style="margin-bottom:6px"><span class="sev-badge" style="background:#3388FF44;color:#3388FF;border:1px solid #3388FF66">' + b.type.toUpperCase() + '</span></div>' +
                            b.desc +
                            '<div class="meta">Lat: ' + b.lat.toFixed(3) + ' | Lon: ' + b.lon.toFixed(3) + '</div>' +
                            '</div></div>';
                    }

                    function getSevClass(sev) {
                        switch(sev) {
                            case 3: return 'sev-critical';
                            case 2: return 'sev-high';
                            case 1: return 'sev-medium';
                            default: return 'sev-low';
                        }
                    }
                    function getSevLabel(sev) {
                        switch(sev) {
                            case 3: return 'CRITICAL';
                            case 2: return 'HIGH';
                            case 1: return 'MEDIUM';
                            default: return 'LOW';
                        }
                    }

                    // ═══════════════════════════════════════
                    //  EVENT MARKERS (called from C#)
                    // ═══════════════════════════════════════

                    function updateMarkers(data) {
                        eventLayer.clearLayers();
                        var eventCount = 0;

                        data.forEach(function(d) {
                            eventCount++;
                            var radius = d.sev === 3 ? 9 : (d.sev === 2 ? 7 : 5);

                            // Pulsing outer ring for critical events
                            if (d.sev === 3) {
                                var pulse = L.circleMarker([d.lat, d.lon], {
                                    radius: 16, color: d.color, fillColor: d.color,
                                    fillOpacity: 0.15, weight: 1, opacity: 0.4,
                                    className: 'pulse-marker'
                                });
                                eventLayer.addLayer(pulse);
                            }

                            var circle = L.circleMarker([d.lat, d.lon], {
                                radius: radius, color: d.color, fillColor: d.color,
                                fillOpacity: 0.85, weight: 1.5
                            });

                            var popupContent = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + d.color + '22;color:' + d.color + ';border-bottom:1px solid ' + d.color + '44">' +
                                d.icon + ' ' + d.title + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px"><span class="sev-badge ' + getSevClass(d.sev) + '">' + getSevLabel(d.sev) + '</span>' +
                                ' <span style="color:#33CCCC;font-size:10px">' + d.cat + '</span></div>' +
                                '<div>' + d.summary + '</div>' +
                                '<div class="meta">' + d.source + ' • ' + d.time + '</div>';

                            if (d.tags) {
                                popupContent += '<div class="tags">' + d.tags + '</div>';
                            }
                            if (d.url) {
                                popupContent += '<a class="open-link" href="' + d.url + '" target="_blank" onclick="window.chrome.webview.postMessage(JSON.stringify({type:\'openUrl\',url:\'' + d.url.replace(/'/g, "\\'") + '\'})); return false;">🔗 OPEN SOURCE</a>';
                            }
                            popupContent += '</div></div>';

                            circle.bindPopup(popupContent, { maxWidth: 380, className: 'dark-popup' });

                            // Click → also notify C# sidebar
                            circle.on('click', function() {
                                try {
                                    window.chrome.webview.postMessage(JSON.stringify({ type: 'selectEvent', id: d.id }));
                                } catch(e) {}
                            });

                            eventLayer.addLayer(circle);
                        });

                        document.getElementById('statEvents').textContent = eventCount;
                    }

                    // ═══════════════════════════════════════
                    //  FLIGHT MARKERS (called from C#)
                    // ═══════════════════════════════════════

                    function updateFlights(data) {
                        flightLayer.clearLayers();
                        var flightCount = 0;

                        data.forEach(function(f) {
                            flightCount++;
                            var acColor = f.sev === 3 ? '#EE3333' : (f.sev === 2 ? '#FF8833' : '#33CCCC');

                            // Aircraft icon with heading rotation
                            var acIcon = L.divIcon({
                                className: 'flight-icon',
                                html: '<div style="transform:rotate(' + (f.hdg || 0) + 'deg);font-size:16px;text-shadow:0 0 6px ' + acColor + '80" title="' + f.callsign + '">✈</div>',
                                iconSize: [18, 18], iconAnchor: [9, 9]
                            });

                            var marker = L.marker([f.lat, f.lon], { icon: acIcon });

                            var popupContent = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + acColor + '22;color:' + acColor + ';border-bottom:1px solid ' + acColor + '44">' +
                                '✈ ' + f.callsign + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px"><span class="sev-badge" style="background:' + acColor + '44;color:' + acColor + ';border:1px solid ' + acColor + '66">' + f.type + '</span></div>' +
                                '<div style="display:grid;grid-template-columns:1fr 1fr;gap:2px 12px;font-size:10px;margin-top:6px">' +
                                '<div>Country:</div><div style="color:#D0D0D0">' + f.country + '</div>' +
                                '<div>Altitude:</div><div style="color:#D0D0D0">' + (f.alt ? Math.round(f.alt).toLocaleString() + ' m' : 'N/A') + '</div>' +
                                '<div>Speed:</div><div style="color:#D0D0D0">' + (f.spd ? Math.round(f.spd) + ' kts' : 'N/A') + '</div>' +
                                '<div>Heading:</div><div style="color:#D0D0D0">' + (f.hdg ? Math.round(f.hdg) + '°' : 'N/A') + '</div>' +
                                '<div>ICAO24:</div><div style="color:#D0D0D0">' + f.icao + '</div>' +
                                '<div>Position:</div><div style="color:#D0D0D0">' + f.lat.toFixed(3) + ', ' + f.lon.toFixed(3) + '</div>' +
                                '</div></div></div>';

                            marker.bindPopup(popupContent, { maxWidth: 320, className: 'dark-popup' });
                            marker.on('click', function() {
                                try {
                                    window.chrome.webview.postMessage(JSON.stringify({ type: 'selectFlight', callsign: f.callsign }));
                                } catch(e) {}
                            });
                            flightLayer.addLayer(marker);
                        });

                        document.getElementById('statFlights').textContent = flightCount;
                    }

                    // ═══════════════════════════════════════
                    //  MISSILE SITES (called from C#)
                    // ═══════════════════════════════════════

                    function updateMissiles(data) {
                        missileLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(m) {
                            count++;

                            // Range circle
                            if (m.range > 0) {
                                L.circle([m.lat, m.lon], {
                                    radius: m.range * 1000,
                                    color: m.color,
                                    fillColor: m.color,
                                    fillOpacity: 0.03,
                                    weight: 1,
                                    dashArray: '6,4',
                                    opacity: 0.4,
                                    interactive: false
                                }).addTo(missileLayer);
                            }

                            // Missile site marker
                            var msIcon = L.divIcon({
                                className: 'missile-marker',
                                html: '<div style="width:12px;height:12px;background:' + m.color + '88;border:2px solid ' + m.color + ';border-radius:50%;box-shadow:0 0 8px ' + m.color + '44" title="' + m.name + '"></div>',
                                iconSize: [12, 12], iconAnchor: [6, 6]
                            });
                            var marker = L.marker([m.lat, m.lon], { icon: msIcon });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + m.color + '22;color:' + m.color + ';border-bottom:1px solid ' + m.color + '44">' +
                                m.icon + ' ' + m.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px">' +
                                '<span class="sev-badge" style="background:' + m.color + '44;color:' + m.color + ';border:1px solid ' + m.color + '66">' + m.type + '</span>' +
                                '</div>' +
                                '<div>' + m.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#33CCCC">Missiles: ' + m.missiles + '</div>' +
                                '<div style="font-size:10px;color:#FF8833">Range: ' + m.range + ' km</div>' +
                                (m.underground ? '<div style="font-size:10px;color:#AA55FF">⛰ UNDERGROUND FACILITY</div>' : '') +
                                '<div class="meta">' + m.country + ' — ' + m.operator + '</div>' +
                                '<div class="meta">Lat: ' + m.lat.toFixed(3) + ' | Lon: ' + m.lon.toFixed(3) + '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 400, className: 'dark-popup' });
                            missileLayer.addLayer(marker);
                        });

                        document.getElementById('statMissiles').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  AIR DEFENSE SYSTEMS (called from C#)
                    // ═══════════════════════════════════════

                    function updateDefense(data) {
                        defenseLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(d) {
                            count++;

                            // Defense coverage circle
                            L.circle([d.lat, d.lon], {
                                radius: d.range * 1000,
                                color: d.color,
                                fillColor: d.color,
                                fillOpacity: 0.04,
                                weight: 1.5,
                                dashArray: '4,4',
                                opacity: 0.5
                            }).addTo(defenseLayer);

                            // Defense site marker (shield shaped)
                            var defIcon = L.divIcon({
                                className: 'defense-marker',
                                html: '<div style="font-size:14px;text-shadow:0 0 6px ' + d.color + '80" title="' + d.name + '">🛡</div>',
                                iconSize: [16, 16], iconAnchor: [8, 8]
                            });
                            var marker = L.marker([d.lat, d.lon], { icon: defIcon });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + d.color + '22;color:' + d.color + ';border-bottom:1px solid ' + d.color + '44">' +
                                '🛡 ' + d.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px">' +
                                '<span class="sev-badge" style="background:' + d.color + '44;color:' + d.color + ';border:1px solid ' + d.color + '66">' + d.system + '</span>' +
                                ' <span style="color:#6A6A80;font-size:10px">' + d.type + '</span>' +
                                '</div>' +
                                '<div>' + d.desc + '</div>' +
                                '<div style="font-size:10px;color:#33CCCC;margin-top:6px">Engagement range: ' + d.range + ' km</div>' +
                                '<div class="meta">' + d.country + '</div>' +
                                '<div class="meta">Lat: ' + d.lat.toFixed(3) + ' | Lon: ' + d.lon.toFixed(3) + '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 380, className: 'dark-popup' });
                            defenseLayer.addLayer(marker);
                        });

                        document.getElementById('statDefense').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  ECLIPSE PATHS (called from C#)
                    // ═══════════════════════════════════════

                    function updateEclipses(data) {
                        eclipseLayer.clearLayers();

                        data.forEach(function(e) {
                            if (e.coords.length < 2) return;

                            // Eclipse path line
                            var path = L.polyline(e.coords, {
                                color: e.color,
                                weight: 3,
                                dashArray: '8,4',
                                opacity: 0.7
                            });

                            // Path shadow (wider, transparent)
                            L.polyline(e.coords, {
                                color: e.color,
                                weight: 40,
                                opacity: 0.05,
                                interactive: false
                            }).addTo(eclipseLayer);

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + e.color + '22;color:' + e.color + ';border-bottom:1px solid ' + e.color + '44">' +
                                '🌑 ' + e.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px">' +
                                '<span class="sev-badge" style="background:#DDCC3344;color:#DDCC33;border:1px solid #DDCC3366">ECLIPSE</span>' +
                                ' <span style="color:#6A6A80;font-size:10px">' + e.date + '</span>' +
                                '</div>' +
                                '<div>' + e.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#FF8833">⚔ ' + e.milSig + '</div>' +
                                '<div class="meta">Magnitude: ' + e.mag.toFixed(3) + ' | ME Coverage: ' + e.coverage + '%</div>' +
                                '</div></div>';

                            path.bindPopup(popupHtml, { maxWidth: 400, className: 'dark-popup' });
                            eclipseLayer.addLayer(path);

                            // Start/end markers
                            var startIcon = L.divIcon({
                                className: 'eclipse-start',
                                html: '<div style="font-size:16px;text-shadow:0 0 8px ' + e.color + '80">🌑</div>',
                                iconSize: [18, 18], iconAnchor: [9, 9]
                            });
                            L.marker(e.coords[0], { icon: startIcon })
                              .bindPopup(popupHtml, { maxWidth: 400, className: 'dark-popup' })
                              .addTo(eclipseLayer);
                        });
                    }

                    // ═══════════════════════════════════════
                    //  HEATMAP (simple density circles)
                    // ═══════════════════════════════════════

                    function updateHeatmap(data) {
                        heatmapLayer.clearLayers();
                        data.forEach(function(d) {
                            L.circle([d.lat, d.lon], {
                                radius: 80000, color: d.color, fillColor: d.color,
                                fillOpacity: 0.08, weight: 0, interactive: false
                            }).addTo(heatmapLayer);
                        });
                    }

                    // ═══════════════════════════════════════
                    //  EQUIPMENT DEPLOYMENTS (called from C#)
                    // ═══════════════════════════════════════

                    function updateEquipment(data) {
                        equipmentLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(eq) {
                            count++;
                            var eqIcon = L.divIcon({
                                className: 'equipment-marker',
                                html: '<div style="font-size:14px;text-shadow:0 0 6px ' + eq.color + '80" title="' + eq.name + '">' + eq.icon + '</div>',
                                iconSize: [16, 16], iconAnchor: [8, 8]
                            });
                            var marker = L.marker([eq.lat, eq.lon], { icon: eqIcon });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + eq.color + '22;color:' + eq.color + ';border-bottom:1px solid ' + eq.color + '44">' +
                                eq.icon + ' ' + eq.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px">' +
                                '<span class="sev-badge" style="background:' + eq.color + '44;color:' + eq.color + ';border:1px solid ' + eq.color + '66">' + eq.designation + '</span>' +
                                ' <span style="color:#6A6A80;font-size:10px">' + eq.domain + '</span>' +
                                '</div>' +
                                '<div style="font-size:11px">' + eq.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#33CCCC">Qty: ' + eq.qty + ' total / ' + eq.active + ' active</div>' +
                                '<div style="font-size:10px;color:#FF8833">Specs: ' + eq.specs + '</div>' +
                                '<div class="meta">' + eq.country + ' — ' + eq.operator + '</div>' +
                                '<div class="meta">Base: ' + eq.base + '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 450, className: 'dark-popup' });
                            equipmentLayer.addLayer(marker);
                        });

                        document.getElementById('statEquipment').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  MARKET IMPACT ZONES (called from C#)
                    // ═══════════════════════════════════════

                    function updateMarketImpacts(data) {
                        marketLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(m) {
                            count++;
                            var color = m.sev >= 3 ? '#EE3333' : (m.sev >= 2 ? '#FF8833' : (m.sev >= 1 ? '#DDCC33' : '#6A6A80'));
                            var radius = m.sev >= 3 ? 80000 : (m.sev >= 2 ? 60000 : 40000);

                            L.circle([m.lat, m.lon], {
                                radius: radius, color: color, fillColor: color,
                                fillOpacity: 0.06, weight: 1.5, dashArray: '6,4'
                            }).addTo(marketLayer);

                            var mIcon = L.divIcon({
                                className: 'market-marker',
                                html: '<div style="font-size:16px;text-shadow:0 0 8px ' + color + '80" title="' + m.name + '">' + m.dir + '</div>',
                                iconSize: [18, 18], iconAnchor: [9, 9]
                            });
                            var marker = L.marker([m.lat, m.lon], { icon: mIcon });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + color + '22;color:' + color + ';border-bottom:1px solid ' + color + '44">' +
                                m.dir + ' ' + m.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:4px"><span class="sev-badge" style="background:' + color + '44;color:' + color + ';border:1px solid ' + color + '66">' + m.sector + '</span></div>' +
                                '<div style="font-size:11px;color:#FF8833">Trigger: ' + m.trigger + '</div>' +
                                '<div style="margin-top:4px;font-size:10px;color:#33CCCC">Assets: ' + m.assets + '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 420, className: 'dark-popup' });
                            marketLayer.addLayer(marker);
                        });

                        document.getElementById('statMarket').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  GROUND FLOCK TRACKING (called from C#)
                    // ═══════════════════════════════════════

                    function updateGroundFlocks(data) {
                        groundLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(f) {
                            count++;

                            // Force deployment radius circle
                            L.circle([f.lat, f.lon], {
                                radius: f.r * 1000,
                                color: f.color,
                                fillColor: f.color,
                                fillOpacity: 0.06,
                                weight: 1.5,
                                dashArray: '6,4',
                                opacity: 0.6
                            }).addTo(groundLayer);

                            // Flock marker
                            var flockIcon = L.divIcon({
                                className: 'ground-marker',
                                html: '<div style="font-size:16px;text-shadow:0 0 8px ' + f.color + '80;cursor:pointer" title="' + f.name + '">' + f.icon + '</div>',
                                iconSize: [18, 18], iconAnchor: [9, 9]
                            });
                            var marker = L.marker([f.lat, f.lon], { icon: flockIcon });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + f.color + '22;color:' + f.color + ';border-bottom:1px solid ' + f.color + '44">' +
                                f.flag + ' ' + f.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px">' +
                                '<span class="sev-badge" style="background:' + f.color + '44;color:' + f.color + ';border:1px solid ' + f.color + '66">' + f.type + '</span>' +
                                ' <span style="color:#6A6A80;font-size:10px">' + f.force + '</span>' +
                                '</div>' +
                                '<div style="font-size:11px">' + f.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#FF8833">Strength: ' + f.strength + '</div>' +
                                '<div style="font-size:10px;color:#33CCCC">Equipment: ' + f.equip + '</div>' +
                                '<div class="meta">' + f.country + ' | Radius: ' + f.r + 'km</div>' +
                                '<div class="meta">Lat: ' + f.lat.toFixed(3) + ' | Lon: ' + f.lon.toFixed(3) + '</div>' +
                                '<div style="margin-top:8px">' +
                                '<a class="open-link" href="#" onclick="openGroundEye(' + f.lat + ',' + f.lon + '); return false;">📷 GROUND EYE VIEW</a>' +
                                '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 450, className: 'dark-popup' });
                            marker.on('click', function() {
                                try { window.chrome.webview.postMessage(JSON.stringify({ type: 'selectFlock', id: f.name })); } catch(e) {}
                            });
                            groundLayer.addLayer(marker);
                        });

                        document.getElementById('statGround').textContent = count;
                    }

                    function updateConflictZones(data) {
                        data.forEach(function(z) {
                            // Conflict zone — pulsing dashed circle
                            var zone = L.circle([z.lat, z.lon], {
                                radius: z.r * 1000,
                                color: z.color,
                                fillColor: z.color,
                                fillOpacity: 0.08,
                                weight: 2,
                                dashArray: '10,6',
                                opacity: 0.7
                            });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + z.color + '22;color:' + z.color + ';border-bottom:1px solid ' + z.color + '44">' +
                                '🎯 ' + z.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px"><span class="sev-badge" style="background:' + z.color + '44;color:' + z.color + ';border:1px solid ' + z.color + '66">' + z.intensity + '</span></div>' +
                                '<div>' + z.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#FF8833">Belligerents: ' + z.belligerents + '</div>' +
                                '<div class="meta">Lat: ' + z.lat.toFixed(3) + ' | Lon: ' + z.lon.toFixed(3) + ' | Radius: ' + z.r + 'km</div>' +
                                '<div style="margin-top:8px">' +
                                '<a class="open-link" href="#" onclick="openGroundEye(' + z.lat + ',' + z.lon + '); return false;">📷 GROUND EYE VIEW</a>' +
                                '</div>' +
                                '</div></div>';

                            zone.bindPopup(popupHtml, { maxWidth: 400, className: 'dark-popup' });
                            groundLayer.addLayer(zone);
                        });
                    }

                    function updateHotspots(data) {
                        data.forEach(function(h) {
                            var color = h.confidence >= 80 ? '#EE3333' : (h.confidence >= 60 ? '#FF8833' : '#DDCC33');
                            var radius = 3 + (h.confidence / 20);

                            // Thermal glow
                            L.circleMarker([h.lat, h.lon], {
                                radius: radius + 6,
                                color: color,
                                fillColor: color,
                                fillOpacity: 0.15,
                                weight: 0,
                                interactive: false
                            }).addTo(groundLayer);

                            var hotspot = L.circleMarker([h.lat, h.lon], {
                                radius: radius,
                                color: color,
                                fillColor: color,
                                fillOpacity: 0.85,
                                weight: 1.5
                            });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + color + '22;color:' + color + ';border-bottom:1px solid ' + color + '44">' +
                                '🔥 ' + h.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div>' + h.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#33CCCC">Confidence: ' + h.confidence + '% | Brightness: ' + h.brightness + 'K</div>' +
                                '<div style="font-size:10px;color:#AA55FF">Source: ' + h.source + ' (' + h.satellite + ')</div>' +
                                '<div class="meta">Detected: ' + h.time + '</div>' +
                                '<div style="margin-top:8px">' +
                                '<a class="open-link" href="#" onclick="openGroundEye(' + h.lat + ',' + h.lon + '); return false;">📷 GROUND EYE VIEW</a>' +
                                '</div>' +
                                '</div></div>';

                            hotspot.bindPopup(popupHtml, { maxWidth: 380, className: 'dark-popup' });
                            groundLayer.addLayer(hotspot);
                        });
                    }

                    function updateCheckpoints(data) {
                        data.forEach(function(c) {
                            var cpIcon = L.divIcon({
                                className: 'checkpoint-marker',
                                html: '<div style="font-size:14px;text-shadow:0 0 6px ' + c.color + '80;cursor:pointer" title="' + c.name + '">' + c.icon + '</div>',
                                iconSize: [16, 16], iconAnchor: [8, 8]
                            });
                            var marker = L.marker([c.lat, c.lon], { icon: cpIcon });

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:' + c.color + '22;color:' + c.color + ';border-bottom:1px solid ' + c.color + '44">' +
                                c.icon + ' ' + c.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div style="margin-bottom:6px"><span class="sev-badge" style="background:' + c.color + '44;color:' + c.color + ';border:1px solid ' + c.color + '66">' + c.type + '</span></div>' +
                                '<div>' + c.desc + '</div>' +
                                '<div class="meta">Controller: ' + c.controller + '</div>' +
                                '<div class="meta">Lat: ' + c.lat.toFixed(3) + ' | Lon: ' + c.lon.toFixed(3) + '</div>' +
                                '<div style="margin-top:8px">' +
                                '<a class="open-link" href="#" onclick="openGroundEye(' + c.lat + ',' + c.lon + '); return false;">📷 GROUND EYE VIEW</a>' +
                                '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 380, className: 'dark-popup' });
                            groundLayer.addLayer(marker);
                        });
                    }

                    function updateCorridors(data) {
                        data.forEach(function(c) {
                            // IDP displacement corridor line
                            var path = L.polyline([[c.startLat, c.startLon], [c.endLat, c.endLon]], {
                                color: '#AA55FF',
                                weight: 3,
                                dashArray: '8,6',
                                opacity: 0.6
                            });

                            // Arrow direction (wider transparent shadow)
                            L.polyline([[c.startLat, c.startLon], [c.endLat, c.endLon]], {
                                color: '#AA55FF',
                                weight: 20,
                                opacity: 0.04,
                                interactive: false
                            }).addTo(groundLayer);

                            var popupHtml = '<div class="event-popup">' +
                                '<div class="popup-header" style="background:#AA55FF22;color:#AA55FF;border-bottom:1px solid #AA55FF44">' +
                                '🏕 ' + c.name + '</div>' +
                                '<div class="popup-body">' +
                                '<div>' + c.desc + '</div>' +
                                '<div style="margin-top:6px;font-size:10px;color:#EE3333">Displaced: ' + c.persons.toLocaleString() + ' persons</div>' +
                                '<div style="font-size:10px;color:#DDCC33">Status: ' + c.status + '</div>' +
                                '</div></div>';

                            path.bindPopup(popupHtml, { maxWidth: 380, className: 'dark-popup' });
                            groundLayer.addLayer(path);

                            // Start marker
                            var startIcon = L.divIcon({
                                className: 'idp-start',
                                html: '<div style="font-size:12px;text-shadow:0 0 6px #AA55FF80">🏕</div>',
                                iconSize: [14, 14], iconAnchor: [7, 7]
                            });
                            L.marker([c.startLat, c.startLon], { icon: startIcon })
                              .bindPopup(popupHtml, { maxWidth: 380, className: 'dark-popup' })
                              .addTo(groundLayer);
                        });
                    }

                    // ═══════════════════════════════════════
                    //  GROUND EYE VIEW (Street-level camera)
                    // ═══════════════════════════════════════

                    function openGroundEye(lat, lon) {
                        try {
                            window.chrome.webview.postMessage(JSON.stringify({
                                type: 'groundEye',
                                lat: lat,
                                lon: lon
                            }));
                        } catch(e) {
                            // Fallback: open Google Maps street view in new tab
                            window.open('https://www.google.com/maps/@' + lat + ',' + lon + ',3a,75y,0h,90t/data=!3m6!1e1!3m4!1s!2e0!7i16384!8i8192', '_blank');
                        }
                    }

                    // ═══════════════════════════════════════
                    //  LIVE STATUS INDICATOR (called from C#)
                    // ═══════════════════════════════════════

                    function updateLiveStatus(isLive, flightCount, cycleCount) {
                        var el = document.getElementById('liveIndicator');
                        if (isLive) {
                            el.style.display = 'block';
                            document.getElementById('liveFlightCount').textContent = flightCount;
                            document.getElementById('liveCycleCount').textContent = cycleCount;
                            el.style.borderColor = '#33CC33';
                            setTimeout(function() { el.style.borderColor = '#33CC3366'; }, 500);
                        } else {
                            el.style.display = 'none';
                        }
                    }

                    // ═══════════════════════════════════════
                    //  GROUND FLOCKS (called from C#)
                    // ═══════════════════════════════════════

                    function updateFlocks(data) {
                        groundLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(f) {
                            count++;

                            // Flock area circle
                            L.circle([f.lat, f.lon], {
                                radius: f.r * 1000,
                                color: f.color,
                                fillColor: f.color,
                                fillOpacity: 0.06,
                                weight: 1.5,
                                dashArray: '8,4',
                                opacity: 0.5
                            }).addTo(groundLayer);

                            // Flock marker
                            var fIcon = L.divIcon({
                                className: 'flock-marker',
                                html: '<div style=""font-size:16px;text-shadow:0 0 8px ' + f.color + '80"" title=""' + f.name + '"">' + f.icon + '</div>',
                                iconSize: [18, 18], iconAnchor: [9, 9]
                            });
                            var marker = L.marker([f.lat, f.lon], { icon: fIcon });

                            var popupHtml = '<div class=""event-popup"">' +
                                '<div class=""popup-header"" style=""background:' + f.color + '22;color:' + f.color + ';border-bottom:1px solid ' + f.color + '44"">' +
                                f.flag + ' ' + f.name + '</div>' +
                                '<div class=""popup-body"">' +
                                '<div style=""margin-bottom:6px"">' +
                                '<span class=""sev-badge"" style=""background:' + f.color + '44;color:' + f.color + ';border:1px solid ' + f.color + '66"">' + f.type + '</span>' +
                                ' <span style=""color:#6A6A80;font-size:10px"">' + f.country + '</span></div>' +
                                '<div>' + f.desc + '</div>' +
                                '<div style=""margin-top:6px;font-size:10px;color:#33CCCC"">Force: ' + f.force + '</div>' +
                                '<div style=""font-size:10px;color:#FF8833"">Strength: ' + f.strength + '</div>' +
                                '<div style=""font-size:10px;color:#DDCC33"">Equipment: ' + f.equip + '</div>' +
                                '<div style=""font-size:10px;color:#AA55FF"">Radius: ' + f.r + ' km</div>' +
                                '<div class=""meta"">Lat: ' + f.lat.toFixed(3) + ' | Lon: ' + f.lon.toFixed(3) + '</div>' +
                                '</div></div>';

                            marker.bindPopup(popupHtml, { maxWidth: 420, className: 'dark-popup' });
                            marker.on('click', function() {
                                try { window.chrome.webview.postMessage(JSON.stringify({ type: 'selectGround', id: f.name })); } catch(e) {}
                            });
                            groundLayer.addLayer(marker);
                        });

                        document.getElementById('statFlocks').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  CONFLICT ZONES (called from C#)
                    // ═══════════════════════════════════════

                    function updateConflictZones(data) {
                        conflictLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(z) {
                            count++;
                            // Pulsing conflict area
                            L.circle([z.lat, z.lon], {
                                radius: z.r * 1000,
                                color: z.color,
                                fillColor: z.color,
                                fillOpacity: 0.1,
                                weight: 2,
                                dashArray: '4,4',
                                opacity: 0.7
                            }).bindPopup('<div class=""event-popup"">' +
                                '<div class=""popup-header"" style=""background:' + z.color + '22;color:' + z.color + ';border-bottom:1px solid ' + z.color + '44"">' +
                                '💥 ' + z.name + '</div>' +
                                '<div class=""popup-body"">' +
                                '<div style=""margin-bottom:6px""><span class=""sev-badge"" style=""background:' + z.color + '44;color:' + z.color + ';border:1px solid ' + z.color + '66"">' + z.intensity + '</span></div>' +
                                '<div>' + z.desc + '</div>' +
                                '<div style=""margin-top:6px;font-size:10px;color:#FF8833"">Belligerents: ' + z.belligerents + '</div>' +
                                '<div class=""meta"">Radius: ' + z.r + ' km</div>' +
                                '</div></div>', { maxWidth: 380, className: 'dark-popup' })
                            .addTo(conflictLayer);
                        });

                        document.getElementById('statConflict').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  THERMAL HOTSPOTS (called from C#)
                    // ═══════════════════════════════════════

                    function updateHotspots(data) {
                        hotspotLayer.clearLayers();
                        var count = 0;

                        data.forEach(function(h) {
                            count++;
                            var radius = h.confidence >= 80 ? 8 : (h.confidence >= 60 ? 6 : 4);

                            // Glow effect
                            if (h.confidence >= 80) {
                                L.circleMarker([h.lat, h.lon], {
                                    radius: 20, color: h.color, fillColor: h.color,
                                    fillOpacity: 0.15, weight: 0
                                }).addTo(hotspotLayer);
                            }

                            L.circleMarker([h.lat, h.lon], {
                                radius: radius, color: h.color, fillColor: h.color,
                                fillOpacity: 0.9, weight: 1.5
                            }).bindPopup('<div class=""event-popup"">' +
                                '<div class=""popup-header"" style=""background:' + h.color + '22;color:' + h.color + ';border-bottom:1px solid ' + h.color + '44"">' +
                                '🔥 ' + h.name + '</div>' +
                                '<div class=""popup-body"">' +
                                '<div>' + h.desc + '</div>' +
                                '<div style=""margin-top:6px;font-size:10px;color:#EE3333"">Brightness: ' + h.brightness + 'K | Confidence: ' + h.confidence + '%</div>' +
                                '<div style=""font-size:10px;color:#33CCCC"">Source: ' + h.source + ' (' + h.satellite + ')</div>' +
                                '<div class=""meta"">' + h.time + '</div>' +
                                '</div></div>', { maxWidth: 350, className: 'dark-popup' })
                            .addTo(hotspotLayer);
                        });

                        document.getElementById('statHotspots').textContent = count;
                    }

                    // ═══════════════════════════════════════
                    //  CHECKPOINTS / FOBs (called from C#)
                    // ═══════════════════════════════════════

                    function updateCheckpoints(data) {
                        checkpointLayer.clearLayers();

                        data.forEach(function(c) {
                            var cpIcon = L.divIcon({
                                className: 'checkpoint-marker',
                                html: '<div style=""font-size:14px;text-shadow:0 0 6px ' + c.color + '80"" title=""' + c.name + '"">' + c.icon + '</div>',
                                iconSize: [16, 16], iconAnchor: [8, 8]
                            });

                            L.marker([c.lat, c.lon], { icon: cpIcon })
                              .bindPopup('<div class=""event-popup"">' +
                                '<div class=""popup-header"" style=""background:' + c.color + '22;color:' + c.color + ';border-bottom:1px solid ' + c.color + '44"">' +
                                c.icon + ' ' + c.name + '</div>' +
                                '<div class=""popup-body"">' +
                                '<div style=""margin-bottom:6px""><span class=""sev-badge"" style=""background:#3388FF44;color:#3388FF;border:1px solid #3388FF66"">' + c.type + '</span></div>' +
                                '<div>' + c.desc + '</div>' +
                                '<div style=""margin-top:6px;font-size:10px;color:#FF8833"">Controller: ' + c.controller + '</div>' +
                                '<div class=""meta"">Lat: ' + c.lat.toFixed(3) + ' | Lon: ' + c.lon.toFixed(3) + '</div>' +
                                '</div></div>', { maxWidth: 380, className: 'dark-popup' })
                              .addTo(checkpointLayer);
                        });
                    }

                    // ═══════════════════════════════════════
                    //  IDP CORRIDORS (called from C#)
                    // ═══════════════════════════════════════

                    function updateCorridors(data) {
                        corridorLayer.clearLayers();

                        data.forEach(function(c) {
                            // Displacement path
                            var path = L.polyline([[c.startLat, c.startLon], [c.endLat, c.endLon]], {
                                color: '#AA55FF',
                                weight: 3,
                                dashArray: '10,6',
                                opacity: 0.6
                            });

                            // Arrow markers at endpoints
                            var startIcon = L.divIcon({
                                className: 'corridor-start',
                                html: '<div style=""font-size:12px;text-shadow:0 0 4px #AA55FF80"">🏚</div>',
                                iconSize: [14, 14], iconAnchor: [7, 7]
                            });
                            var endIcon = L.divIcon({
                                className: 'corridor-end',
                                html: '<div style=""font-size:12px;text-shadow:0 0 4px #AA55FF80"">🏕</div>',
                                iconSize: [14, 14], iconAnchor: [7, 7]
                            });

                            var popupHtml = '<div class=""event-popup"">' +
                                '<div class=""popup-header"" style=""background:#AA55FF22;color:#AA55FF;border-bottom:1px solid #AA55FF44"">' +
                                '🏕 ' + c.name + '</div>' +
                                '<div class=""popup-body"">' +
                                '<div>' + c.desc + '</div>' +
                                '<div style=""margin-top:6px;font-size:10px;color:#FF8833"">Displaced: ' + c.persons.toLocaleString() + '</div>' +
                                '<div style=""font-size:10px;color:#33CC33"">Status: ' + c.status + '</div>' +
                                '</div></div>';

                            path.bindPopup(popupHtml, { maxWidth: 350, className: 'dark-popup' });
                            corridorLayer.addLayer(path);
                            L.marker([c.startLat, c.startLon], { icon: startIcon }).bindPopup(popupHtml).addTo(corridorLayer);
                            L.marker([c.endLat, c.endLon], { icon: endIcon }).bindPopup(popupHtml).addTo(corridorLayer);
                        });
                    }

                    // ═══════════════════════════════════════
                    //  FLY-TO (called from C# on event select)
                    // ═══════════════════════════════════════

                    function flyTo(lat, lon, zoom) {
                        map.flyTo([lat, lon], zoom || 8, { duration: 1.2 });
                    }

                    // Coordinate display on mouse move
                    var coordDiv = document.createElement('div');
                    coordDiv.style.cssText = 'position:absolute;bottom:12px;right:12px;z-index:1000;background:#080810ee;border:1px solid #2A2A3E;border-radius:3px;padding:4px 8px;font-size:9px;color:#6A6A80;font-family:Consolas,monospace';
                    document.body.appendChild(coordDiv);
                    map.on('mousemove', function(e) {
                        coordDiv.textContent = e.latlng.lat.toFixed(4) + ', ' + e.latlng.lng.toFixed(4);
                    });

                    // Right-click context
                    map.on('contextmenu', function(e) {
                        L.popup()
                         .setLatLng(e.latlng)
                         .setContent('<div style="font-family:Consolas;font-size:11px;color:#D0D0D0;padding:4px"><b style="color:#3388FF">📍 Coordinates</b><br>' +
                             'Lat: ' + e.latlng.lat.toFixed(5) + '<br>Lon: ' + e.latlng.lng.toFixed(5) + '<br>' +
                             '<span style="color:#6A6A80;font-size:9px">Right-click to pin location</span></div>')
                         .openOn(map);
                    });
                </script>
            </body>
            </html>
            """;
        }

        // ═══════════════════════════════════════════
        //  ECLIPSE MONITORING
        // ═══════════════════════════════════════════

        private void LoadEclipseData()
        {
            var eclipses = _eclipse.GetAllEclipses();
            var vms = eclipses.Select(e => new EclipseViewModel(e)).ToList();
            lstEclipses.ItemsSource = vms;
            UpdateEclipseCountdown();
        }

        private void StartEclipseCountdown()
        {
            _eclipseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _eclipseTimer.Tick += (_, _) => UpdateEclipseCountdown();
            _eclipseTimer.Start();
        }

        private void UpdateEclipseCountdown()
        {
            var (next, countdown) = _eclipse.GetNextEclipseCountdown();
            if (next != null)
            {
                var icon = next.Type switch
                {
                    EclipseType.SolarTotal => "🌑",
                    EclipseType.SolarAnnular => "🌗",
                    EclipseType.SolarPartial => "🌘",
                    EclipseType.LunarTotal => "🌕",
                    EclipseType.LunarPartial => "🌔",
                    _ => "🌙"
                };
                txtNextEclipse.Text = $"{icon} {next.Name} — {next.Date:yyyy-MM-dd HH:mm UTC} — Coverage: {next.MECoveragePercent}% ME";
                txtEclipseCountdown.Text = $"T-{countdown.Days}d {countdown.Hours:D2}h {countdown.Minutes:D2}m {countdown.Seconds:D2}s";
                txtEclipseMilSig.Text = next.MilitarySignificance;
            }
            else
            {
                txtNextEclipse.Text = "No upcoming ME-visible eclipses in database";
                txtEclipseCountdown.Text = "";
                txtEclipseMilSig.Text = "";
            }

            // Check for active eclipse — flash alert
            var active = _eclipse.GetActiveEclipse();
            if (active != null)
            {
                txtEclipseCountdown.Text = "⚡ ECLIPSE IN PROGRESS";
                txtEclipseCountdown.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0x33, 0x33));
            }
            else
            {
                txtEclipseCountdown.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x33));
            }
        }

        private void BtnRefreshEclipses_Click(object sender, RoutedEventArgs e) => LoadEclipseData();

        private void LstEclipses_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstEclipses.SelectedItem is EclipseViewModel vm && vm.Eclipse.PathCoordinates.Count > 0)
            {
                // Fly to eclipse path center on map
                var mid = vm.Eclipse.PathCoordinates[vm.Eclipse.PathCoordinates.Count / 2];
                try
                {
                    webMap.CoreWebView2?.ExecuteScriptAsync($"flyTo({mid.Lat},{mid.Lon},4)");
                    tabMain.SelectedIndex = 1; // Switch to map tab
                }
                catch { }
            }
        }

        // ═══════════════════════════════════════════
        //  MISSILE / DEFENSE DATA
        // ═══════════════════════════════════════════

        private void LoadMissileDefenseData()
        {
            dgMissiles.ItemsSource = _missiles.GetAllMissileSites();
            dgDefense.ItemsSource = _missiles.GetAllAirDefenseSites();
        }

        private void BtnShowMissilesOnMap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateMapMissiles();
                UpdateMapDefense();
                tabMain.SelectedIndex = 1; // Switch to Map tab
                webMap.CoreWebView2?.ExecuteScriptAsync("flyTo(30, 50, 5)");
                txtStatus.Text = "🚀 Missile sites & air defense systems displayed on map";
            }
            catch { }
        }

        private void UpdateMapMissiles()
        {
            try
            {
                var data = _missiles.BuildMissileMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateMissiles([{data}])");
            }
            catch { }
        }

        private void UpdateMapDefense()
        {
            try
            {
                var data = _missiles.BuildDefenseMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateDefense([{data}])");
            }
            catch { }
        }

        private void UpdateMapEclipses()
        {
            try
            {
                var eclipses = _eclipse.GetMEVisibleEclipses();
                var data = string.Join(",", eclipses.Where(e => e.PathCoordinates.Count > 0).Select(e =>
                {
                    var coords = string.Join(",", e.PathCoordinates.Select(p => $"[{p.Lat},{p.Lon}]"));
                    var name = Escape(e.Name);
                    var desc = Escape(e.Description);
                    var milSig = Escape(e.MilitarySignificance);
                    var date = e.Date.ToString("yyyy-MM-dd HH:mm UTC");
                    var color = e.Type switch
                    {
                        EclipseType.SolarTotal => "#DDCC33",
                        EclipseType.SolarAnnular => "#FF8833",
                        EclipseType.SolarPartial => "#AA55FF",
                        _ => "#6A6A80"
                    };
                    return $"{{coords:[{coords}],name:'{name}',desc:'{desc}',milSig:'{milSig}'," +
                           $"date:'{date}',color:'{color}',mag:{e.MaxMagnitude},coverage:{e.MECoveragePercent}}}";
                }));
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateEclipses([{data}])");
            }
            catch { }
        }

        // ═══════════════════════════════════════════
        //  AUTO-REFRESH
        // ═══════════════════════════════════════════

        private void CmbInterval_Changed(object sender, SelectionChangedEventArgs e)
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer = null;

            if (cmbInterval.SelectedItem is ComboBoxItem item)
            {
                var text = item.Content?.ToString() ?? "OFF";
                if (text == "OFF") return;

                var minutes = text switch
                {
                    "1 min" => 1,
                    "5 min" => 5,
                    "15 min" => 15,
                    "30 min" => 30,
                    _ => 0
                };

                if (minutes > 0)
                {
                    _autoRefreshTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMinutes(minutes)
                    };
                    _autoRefreshTimer.Tick += async (_, _) => await RefreshAllAsync();
                    _autoRefreshTimer.Start();
                    txtStatus.Text = $"Auto-refresh: every {minutes} min";
                }
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAllAsync();

        // ═══════════════════════════════════════════
        //  LIVE TRACKING (30-second flight polling)
        // ═══════════════════════════════════════════

        private void BtnToggleLiveTracking_Click(object sender, RoutedEventArgs e)
        {
            _liveTrackingEnabled = !_liveTrackingEnabled;

            if (_liveTrackingEnabled)
            {
                _liveTrackingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                _liveTrackingTimer.Tick += async (_, _) => await LiveTrackingPollAsync();
                _liveTrackingTimer.Start();
                txtLiveStatus.Text = "● LIVE";
                txtLiveStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x33));
                btnLiveTracking.Content = "⏹ STOP LIVE";
                txtStatus.Text = "🔴 LIVE tracking enabled — polling flights every 60s";
                _ = LiveTrackingPollAsync(); // Immediate first poll
            }
            else
            {
                _liveTrackingTimer?.Stop();
                _liveTrackingTimer = null;
                _liveTrackingCycles = 0;
                txtLiveStatus.Text = "○ OFFLINE";
                txtLiveStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x80));
                btnLiveTracking.Content = "▶ GO LIVE";
                txtStatus.Text = "Live tracking stopped";
            }
        }

        private async Task LiveTrackingPollAsync()
        {
            try
            {
                _liveTrackingCycles++;
                var flights = await _flights.FetchMilitaryFlightsAsync();
                _lastFlights = flights;
                _lastFlightScan = DateTime.UtcNow;

                Dispatcher.Invoke(() =>
                {
                    dgFlights.ItemsSource = flights;
                    var label = _flights.IsRateLimited && flights.Count > 0
                        ? $"{flights.Count} aircraft (cached) | Scan #{_liveTrackingCycles}"
                        : _flights.IsRateLimited
                        ? $"Rate-limited — cooldown | Scan #{_liveTrackingCycles}"
                        : $"{flights.Count} aircraft | Scan #{_liveTrackingCycles}";
                    txtFlightCount.Text = label;
                    UpdateMapFlights(flights);

                    // Update flight events in timeline
                    _allEvents.RemoveAll(e2 => e2.DataSource == DataSource.FlightTracker);
                    foreach (var f in flights)
                    {
                        var ev = _flights.ToConflictEvent(f);
                        if (ev != null)
                        {
                            _alerts.EvaluateEvent(ev);
                            _allEvents.Add(ev);
                        }
                    }

                    var elapsed = DateTime.UtcNow - _lastFlightScan;
                    txtLiveStatus.Text = $"● LIVE ({_liveTrackingCycles})";
                    txtLastUpdate.Text = $"Flight scan: {DateTime.Now:HH:mm:ss}";
                    webMap.CoreWebView2?.ExecuteScriptAsync($"updateLiveStatus(true,{flights.Count},{_liveTrackingCycles})");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    txtLiveStatus.Text = "⚠ LIVE ERROR";
                    txtLiveStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0x33, 0x33));
                    txtStatus.Text = $"⚠ Live tracking error: {ex.Message}";
                });
            }
        }

        // ═══════════════════════════════════════════
        //  EQUIPMENT DATABASE
        // ═══════════════════════════════════════════

        private void LoadEquipmentData()
        {
            var forces = _equipment.GetAllForces();
            lstForceOrbat.ItemsSource = forces.Select(f => new ForceViewModel(f)).ToList();
            dgEquipment.ItemsSource = _equipment.GetAllEquipment();

            var stats = _equipment.GetStats();
            txtEquipmentStats.Text = $"{stats.Countries} forces | {stats.TotalTypes} weapon systems | {stats.TotalActive:N0} active units | " +
                                     $"✈ {stats.Aircraft:N0} aircraft | 🛩 {stats.Drones:N0} drones | 🚢 {stats.Naval:N0} naval | 🚀 {stats.Missiles:N0} missiles";
        }

        private void BtnShowEquipmentOnMap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateMapEquipment();
                tabMain.SelectedIndex = 1; // Switch to Map
                webMap.CoreWebView2?.ExecuteScriptAsync("flyTo(30, 47, 5)");
                txtStatus.Text = $"⚙ Equipment deployments displayed on map ({_equipment.GetTotalActiveCount():N0} active assets)";
            }
            catch { }
        }

        private void UpdateMapEquipment()
        {
            try
            {
                var data = _equipment.BuildEquipmentMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateEquipment([{data}])");
            }
            catch { }
        }

        private void CmbEquipmentFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            if (cmbEquipmentFilter.SelectedItem is ComboBoxItem item)
            {
                var filter = item.Content?.ToString() ?? "ALL";
                List<MilitaryEquipment> filtered;

                if (filter == "ALL")
                    filtered = _equipment.GetAllEquipment();
                else if (filter == "CRITICAL")
                    filtered = _equipment.GetCriticalAssets();
                else if (Enum.TryParse<EquipmentDomain>(filter.Replace(" ", ""), true, out var domain))
                    filtered = _equipment.GetEquipmentByDomain(domain);
                else
                    filtered = _equipment.GetAllEquipment();

                dgEquipment.ItemsSource = filtered;
            }
        }

        private void LstForceOrbat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstForceOrbat.SelectedItem is ForceViewModel vm)
            {
                dgEquipment.ItemsSource = _equipment.GetEquipmentByCountry(vm.Country);
            }
        }

        // ═══════════════════════════════════════════
        //  WARZONE RADIO STREAMS
        // ═══════════════════════════════════════════

        private void LoadRadioStreams()
        {
            var streams = _radio.GetAllStreams();
            dgRadio.ItemsSource = streams;

            var stats = _radio.GetStats();
            txtRadioStats.Text = $"{stats.Total} streams | {stats.Live} live | " +
                                 $"📡 {stats.News} news | 🌍 {stats.Regional} regional | " +
                                 $"🎖 {stats.Military} mil briefs | 📻 {stats.Scanner} scanner | 🏥 {stats.Humanitarian} humanitarian";
        }

        private void CmbRadioFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            if (cmbRadioFilter.SelectedItem is ComboBoxItem item)
            {
                var filter = item.Content?.ToString() ?? "ALL";
                List<RadioStream> filtered;

                if (filter == "ALL")
                    filtered = _radio.GetAllStreams();
                else if (filter == "LIVE ONLY")
                    filtered = _radio.GetLiveStreams();
                else if (Enum.TryParse<RadioCategory>(filter.Replace(" ", ""), true, out var cat))
                    filtered = _radio.GetByCategory(cat);
                else
                    filtered = _radio.GetAllStreams();

                dgRadio.ItemsSource = filtered;
            }
        }

        private void BtnOpenStream_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is RadioStream stream)
            {
                try
                {
                    var url = !string.IsNullOrEmpty(stream.WebUrl) ? stream.WebUrl : stream.Url;
                    if (!string.IsNullOrEmpty(url))
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                        txtStatus.Text = $"📻 Opened: {stream.Name}";
                    }
                }
                catch { }
            }
        }

        private void DgRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgRadio.SelectedItem is RadioStream stream)
            {
                txtRadioDetail.Text = $"{stream.Name}\n{stream.Description}\n\nRegion: {stream.Region}\n" +
                                      $"Language: {stream.Language}\nCategory: {stream.CategoryDisplay}\n" +
                                      $"Status: {stream.StatusDisplay}\n\n" +
                                      (string.IsNullOrEmpty(stream.Url) ? "Web access only — click OPEN to launch" : $"Stream: {stream.Url}");
            }
        }

        // ═══════════════════════════════════════════
        //  GROUND FLOCK TRACKING
        // ═══════════════════════════════════════════

        private void LoadGroundData()
        {
            var flocks = _ground.GetAllFlocks();
            dgFlocks.ItemsSource = flocks;

            var zones = _ground.GetAllConflictZones();
            dgConflictZones.ItemsSource = zones;

            var hotspots = _ground.GetAllHotspots();
            dgThermal.ItemsSource = hotspots;

            var checkpoints = _ground.GetAllCheckpoints();
            dgCheckpoints.ItemsSource = checkpoints;

            var corridors = _ground.GetAllCorridors();
            dgCorridors.ItemsSource = corridors;

            var stats = _ground.GetStats();
            txtGroundStats.Text = $"{stats.ActiveFlocks} active flocks | {stats.ConflictZones} conflict zones ({stats.HighIntensityZones} HIGH) | " +
                                  $"{stats.ThermalHotspots} thermal hotspots | {stats.Checkpoints} checkpoints/FOBs | " +
                                  $"{stats.IdpCorridors} IDP corridors ({stats.TotalDisplaced:N0} displaced) | {stats.Countries} countries";
        }

        private void CmbGroundFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            if (cmbGroundFilter.SelectedItem is ComboBoxItem item)
            {
                var filter = item.Content?.ToString() ?? "ALL";
                List<GroundFlock> filtered;

                if (filter == "ALL")
                    filtered = _ground.GetAllFlocks();
                else if (filter == "ACTIVE ONLY")
                    filtered = _ground.GetActiveFlocks();
                else if (Enum.TryParse<GroundFlockType>(filter.Replace(" ", ""), true, out var type))
                    filtered = _ground.GetFlocksByType(type);
                else
                    filtered = _ground.GetAllFlocks();

                dgFlocks.ItemsSource = filtered;
            }
        }

        private void BtnShowGroundOnMap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateMapGround();
                tabMain.SelectedIndex = 1; // Switch to Map
                webMap.CoreWebView2?.ExecuteScriptAsync("flyTo(30, 44, 5)");
                txtStatus.Text = $"🪖 Ground flocks, conflict zones, hotspots, and checkpoints displayed on map";
            }
            catch { }
        }

        private void UpdateMapGround()
        {
            try
            {
                var flockData = _ground.BuildFlockMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateGroundFlocks([{flockData}])");

                var zoneData = _ground.BuildConflictZoneMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateConflictZones([{zoneData}])");

                var hotspotData = _ground.BuildHotspotMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateHotspots([{hotspotData}])");

                var checkpointData = _ground.BuildCheckpointMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateCheckpoints([{checkpointData}])");

                var corridorData = _ground.BuildCorridorMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateCorridors([{corridorData}])");
            }
            catch { }
        }

        private void DgFlocks_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgFlocks.SelectedItem is GroundFlock flock)
            {
                txtGroundDetail.Text = $"{flock.ForceFlag} {flock.Name}\n" +
                                       $"Force: {flock.Force}\nCountry: {flock.Country}\n" +
                                       $"Type: {flock.TypeDisplay}\nThreat: {flock.ThreatDisplay}\n" +
                                       $"Strength: {flock.EstimatedStrength}\nRadius: {flock.RadiusKm} km\n\n" +
                                       $"Equipment: {flock.Equipment}\n\n" +
                                       $"{flock.Description}\n\n" +
                                       $"Coords: {flock.Latitude:F3}, {flock.Longitude:F3}\nUpdated: {flock.AgeDisplay}";

                // Load embedded ground eye preview
                LoadEmbeddedGroundEye(flock.Latitude, flock.Longitude, flock.Name);

                // Fly to location on map
                try
                {
                    webMap.CoreWebView2?.ExecuteScriptAsync($"flyTo({flock.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},{flock.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)},8)");
                }
                catch { }
            }
        }

        /// <summary>Opens ground-eye view from the 📷 button in each flock row.</summary>
        private void BtnGroundEyeFlock_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.DataContext is GroundFlock flock)
            {
                OpenGroundEyeWindow(flock.Latitude, flock.Longitude, flock.Name);
            }
        }

        // ═══════════════════════════════════════════
        //  WAR ECONOMY / MARKET IMPACTS
        // ═══════════════════════════════════════════

        private void LoadMarketData()
        {
            dgMarketImpacts.ItemsSource = _market.GetAllImpacts();
            dgCommodities.ItemsSource = _market.GetAllCommodities();
            dgDefenseStocks.ItemsSource = _market.GetAllDefenseStocks();
            lstMarketAlerts.ItemsSource = _market.GetAllAlerts();
            dgSanctions.ItemsSource = _market.GetAllSanctions();
            dgTradeFeeds.ItemsSource = _market.GetAllFeeds();

            var stats = _market.GetStats();
            txtMarketStats.Text = $"📈 {stats.TotalImpacts} impacts ({stats.CriticalImpacts} CRITICAL, {stats.HighImpacts} HIGH) | " +
                                  $"{stats.Sectors} sectors | {stats.Commodities} commodities | {stats.DefenseStocks} defense stocks | " +
                                  $"🚨 {stats.ActiveAlerts} alerts | 🏛 {stats.Sanctions} sanctions | 📡 {stats.LiveFeeds} live feeds";
        }

        private void CmbMarketFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            if (cmbMarketFilter.SelectedItem is ComboBoxItem item)
            {
                var filter = item.Content?.ToString() ?? "ALL";
                if (filter == "ALL")
                    dgMarketImpacts.ItemsSource = _market.GetAllImpacts();
                else if (Enum.TryParse<MarketSector>(filter.Replace(" ", ""), true, out var sector))
                    dgMarketImpacts.ItemsSource = _market.GetImpactsBySector(sector);
                else
                    dgMarketImpacts.ItemsSource = _market.GetAllImpacts();
            }
        }

        private void DgMarketImpacts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgMarketImpacts.SelectedItem is MarketImpact impact)
            {
                txtMarketDetail.Text = $"{impact.SeverityDisplay}  {impact.DirectionDisplay}  {impact.SectorDisplay}\n\n" +
                                       $"EVENT: {impact.Title}\n" +
                                       $"TRIGGER: {impact.Trigger}\n\n" +
                                       $"MARKET IMPACT:\n{impact.Impact}\n\n" +
                                       $"ASSETS TO WATCH: {impact.Assets}\n\n" +
                                       $"PROBABILITY: {impact.Probability}\n" +
                                       $"TIME HORIZON: {impact.TimeHorizon}\n" +
                                       $"REGION: {impact.Region}\n\n" +
                                       $"HISTORICAL PRECEDENT:\n{impact.HistoricalPrecedent}";
            }
        }

        private void BtnShowMarketOnMap_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UpdateMapMarket();
                tabMain.SelectedIndex = 1; // Switch to Map
                webMap.CoreWebView2?.ExecuteScriptAsync("flyTo(30, 44, 4)");
                txtStatus.Text = $"📈 Market impact zones displayed on map";
            }
            catch { }
        }

        private void UpdateMapMarket()
        {
            try
            {
                var data = _market.BuildImpactMapData();
                webMap.CoreWebView2?.ExecuteScriptAsync($"updateMarketImpacts([{data}])");
            }
            catch { }
        }

        private void BtnOpenMarketLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DefenseStock stock && !string.IsNullOrEmpty(stock.LiveUrl))
            {
                try { Process.Start(new ProcessStartInfo(stock.LiveUrl) { UseShellExecute = true }); }
                catch { }
            }
        }

        private void BtnOpenTradeFeed_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TradeFeed feed && !string.IsNullOrEmpty(feed.Url))
            {
                try { Process.Start(new ProcessStartInfo(feed.Url) { UseShellExecute = true }); }
                catch { }
            }
        }


        /// <summary>Opens a ground-eye view camera using Google Street View / Mapillary embed in a new WebView2 window.</summary>
        private void BtnGroundEyeView_Click(object sender, RoutedEventArgs e)
        {
            // Get selected flock location or use default AOR center
            double lat = 30.0, lon = 44.0;
            string locationName = "Middle East AOR";

            if (dgFlocks.SelectedItem is GroundFlock flock)
            {
                lat = flock.Latitude;
                lon = flock.Longitude;
                locationName = flock.Name;
            }
            else if (dgConflictZones.SelectedItem is GroundConflictZone zone)
            {
                lat = zone.Latitude;
                lon = zone.Longitude;
                locationName = zone.Name;
            }
            else if (dgCheckpoints.SelectedItem is GroundCheckpoint cp)
            {
                lat = cp.Latitude;
                lon = cp.Longitude;
                locationName = cp.Name;
            }

            OpenGroundEyeWindow(lat, lon, locationName);
        }

        /// <summary>Loads embedded ground-eye preview in the bottom panel WebView2.</summary>
        private void LoadEmbeddedGroundEye(double lat, double lon, string name)
        {
            try
            {
                txtGroundEyeLabel.Visibility = Visibility.Collapsed;
                webGroundEye.Visibility = Visibility.Visible;
                var latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var html = """
<!DOCTYPE html><html><head><meta charset='utf-8'/>
<style>*{margin:0;padding:0}body{background:#080810;overflow:hidden}
#map{width:100%;height:100%}.leaflet-control-attribution{display:none!important}</style>
<link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
<script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script></head>
<body><div id='map' style='width:100%;height:100vh'></div><script>
var m=L.map('map',{zoomControl:false}).setView([__LAT__,__LON__],16);
L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',{maxZoom:19}).addTo(m);
L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}{r}.png',{maxZoom:19,subdomains:'abcd',opacity:0.7}).addTo(m);
var ti=L.divIcon({className:'t',html:'<div style="width:30px;height:30px;border:2px solid #33CC3388;border-radius:50%;position:relative"><div style="position:absolute;top:50%;left:-6px;right:-6px;height:1px;background:#33CC3366"></div><div style="position:absolute;left:50%;top:-6px;bottom:-6px;width:1px;background:#33CC3366"></div><div style="position:absolute;top:50%;left:50%;width:4px;height:4px;background:#33CC33;border-radius:50%;transform:translate(-50%,-50%)"></div></div>',iconSize:[30,30],iconAnchor:[15,15]});
L.marker([__LAT__,__LON__],{icon:ti}).addTo(m);
</script></body></html>
""".Replace("__LAT__", latStr).Replace("__LON__", lonStr);
                _ = InitGroundEyePreviewAsync(html);
            }
            catch { }
        }

        private async Task InitGroundEyePreviewAsync(string html)
        {
            try
            {
                await webGroundEye.EnsureCoreWebView2Async();
                webGroundEye.CoreWebView2.NavigateToString(html);
            }
            catch { }
        }

        /// <summary>Opens a standalone Ground Eye View window.</summary>
        private void OpenGroundEyeWindow(double lat, double lon, string locationName)
        {
            var streetViewHtml = BuildStreetViewHtml(lat, lon, locationName);

            var window = new Window
            {
                Title = $"N01D :: GROUND EYE — {locationName}",
                Width = 1100,
                Height = 700,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x10))
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header bar
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x25)),
                Padding = new Thickness(12, 8, 12, 8)
            };
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"👁 GROUND EYE VIEW — {locationName}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0xCC, 0x33)),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"  |  {lat:F4}, {lon:F4}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });
            header.Child = headerPanel;
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            // WebView2 for street view
            var wv = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 8, 8, 16)
            };
            Grid.SetRow(wv, 1);
            grid.Children.Add(wv);

            window.Content = grid;
            window.Show();

            // Initialize WebView2 and navigate
            _ = InitStreetViewAsync(wv, streetViewHtml);
        }

        private async Task InitStreetViewAsync(Microsoft.Web.WebView2.Wpf.WebView2 wv, string html)
        {
            try
            {
                await wv.EnsureCoreWebView2Async();
                wv.CoreWebView2.NavigateToString(html);
            }
            catch { }
        }

        private static string BuildStreetViewHtml(double lat, double lon, string locationName)
        {
            var latStr = lat.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var lonStr = lon.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var escapedName = Escape(locationName);

            return """
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8'/>
    <style>
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body { background: #080810; font-family: Consolas, monospace; color: #D0D0D0; overflow: hidden; }
        .toolbar {
            background: #10101C; border-bottom: 1px solid #2A2A3E; padding: 8px 14px;
            display: flex; align-items: center; gap: 12px; font-size: 11px;
        }
        .toolbar button {
            background: #161625; color: #3388FF; border: 1px solid #2A2A3E;
            padding: 4px 12px; border-radius: 3px; cursor: pointer;
            font-family: Consolas; font-size: 11px;
        }
        .toolbar button:hover { background: #1E1E35; border-color: #3388FF; }
        .toolbar button.active { border-color: #33CC33; color: #33CC33; }
        .toolbar .coords { color: #6A6A80; }
        .toolbar .label { color: #33CCCC; font-weight: bold; }
        #viewer { width: 100vw; height: calc(100vh - 38px); }
        .source-panel {
            position: absolute; bottom: 12px; right: 12px; z-index: 1000;
            background: #080810ee; border: 1px solid #2A2A3E; border-radius: 4px;
            padding: 10px 14px; font-size: 10px; backdrop-filter: blur(8px);
        }
        .source-panel h4 { color: #AA55FF; margin-bottom: 6px; font-size: 11px; }
        .source-panel a {
            display: block; color: #3388FF; text-decoration: none; padding: 2px 0;
        }
        .source-panel a:hover { color: #33CCCC; }
        .compass {
            position: absolute; top: 60px; right: 12px; z-index: 1000;
            width: 60px; height: 60px; border-radius: 50%;
            background: #080810ee; border: 1px solid #2A2A3E;
            display: flex; align-items: center; justify-content: center;
            font-size: 22px;
        }
        #heading-display { color: #33CC33; font-size: 10px; text-align: center; }
        .nv-overlay {
            position: absolute; top: 0; left: 0; right: 0; bottom: 0;
            pointer-events: none; z-index: 500; display: none;
            background: radial-gradient(ellipse at center, transparent 40%, rgba(0,20,0,0.5) 100%);
            mix-blend-mode: multiply;
        }
        .nv-scanline {
            position: absolute; top: 0; left: 0; right: 0; bottom: 0;
            pointer-events: none; z-index: 501; display: none;
            background: repeating-linear-gradient(
                0deg, transparent, transparent 2px, rgba(0,60,0,0.08) 2px, rgba(0,60,0,0.08) 4px
            );
        }
    </style>
    <link rel='stylesheet' href='https://unpkg.com/leaflet@1.9.4/dist/leaflet.css'/>
    <script src='https://unpkg.com/leaflet@1.9.4/dist/leaflet.js'></script>
</head>
<body>
    <div class='toolbar'>
        <span class='label'>&#x1F441; GROUND EYE</span>
        <button id='btnSat' class='active' onclick="switchView('sat')">&#x1F6F0; SATELLITE</button>
        <button id='btnStreet' onclick="switchView('street')">&#x1F4F7; STREET VIEW</button>
        <button id='btnWebcam' onclick="switchView('webcam')">&#x1F4F9; WEBCAMS</button>
        <button id='btnNV' onclick='toggleNV()'>&#x1F319; NIGHT VISION</button>
        <span class='coords' id='coordsDisplay'>__LAT__, __LON__</span>
    </div>
    <div class='nv-overlay' id='nvOverlay'></div>
    <div class='nv-scanline' id='nvScanline'></div>
    <div id='viewer'></div>

    <div class='compass' id='compass'>&#x1F9ED;</div>

    <div class='source-panel'>
        <h4>&#x1F4E1; SOURCES</h4>
        <a href='https://www.google.com/maps/@__LAT__,__LON__,18z' target='_blank'>&#x1F5FA; Google Maps</a>
        <a href='https://www.google.com/maps/@?api=1&amp;map_action=pano&amp;viewpoint=__LAT__,__LON__' target='_blank'>&#x1F4F7; Google Street View</a>
        <a href='https://www.mapillary.com/app/?lat=__LAT__&amp;lng=__LON__&amp;z=16' target='_blank'>&#x1F4F8; Mapillary</a>
        <a href='https://www.openstreetmap.org/#map=16/__LAT__/__LON__' target='_blank'>&#x1F30D; OpenStreetMap</a>
        <a href='https://zoom.earth/#view=__LAT__,__LON__,16z' target='_blank'>&#x1F6F0; Zoom Earth</a>
        <a href='https://livingatlas.arcgis.com/wayback/#active=54110&amp;mapCenter=__LON__%2C__LAT__%2C16' target='_blank'>&#x1F570; Esri Wayback</a>
    </div>

    <script>
        var currentLat = __LAT__;
        var currentLon = __LON__;
        var nvActive = false;
        var currentView = 'sat';
        var map = null;

        function initSatView() {
            document.getElementById('viewer').innerHTML = '';
            map = L.map('viewer', {
                zoomControl: true, minZoom: 3, maxZoom: 19
            }).setView([currentLat, currentLon], 16);

            L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
                attribution: 'Esri World Imagery | N01D Ground Eye',
                maxZoom: 19
            }).addTo(map);

            L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}{r}.png', {
                maxZoom: 19, subdomains: 'abcd', opacity: 0.7
            }).addTo(map);

            var targetIcon = L.divIcon({
                className: 'target-marker',
                html: '<div style="width:40px;height:40px;border:2px solid #33CC3388;border-radius:50%;position:relative">' +
                      '<div style="position:absolute;top:50%;left:-8px;right:-8px;height:1px;background:#33CC3366"></div>' +
                      '<div style="position:absolute;left:50%;top:-8px;bottom:-8px;width:1px;background:#33CC3366"></div>' +
                      '<div style="position:absolute;top:50%;left:50%;width:6px;height:6px;background:#33CC33;border-radius:50%;transform:translate(-50%,-50%)"></div></div>',
                iconSize: [40, 40], iconAnchor: [20, 20]
            });
            L.marker([currentLat, currentLon], { icon: targetIcon }).addTo(map)
              .bindPopup('<div style="font-family:Consolas;font-size:11px;color:#D0D0D0;padding:4px">' +
                '<b style="color:#33CC33">&#x1F441; __LOCNAME__</b><br>' +
                'Lat: __LAT__<br>Lon: __LON__</div>');

            map.on('mousemove', function(e) {
                document.getElementById('coordsDisplay').textContent = e.latlng.lat.toFixed(5) + ', ' + e.latlng.lng.toFixed(5);
            });

            var measureStart = null;
            var measureLine = null;
            map.on('contextmenu', function(e) {
                if (!measureStart) {
                    measureStart = e.latlng;
                    L.popup().setLatLng(e.latlng)
                        .setContent('<div style="font-family:Consolas;font-size:10px;color:#33CC33;padding:2px">&#x1F4CF; Click another point to measure distance</div>')
                        .openOn(map);
                } else {
                    var dist = measureStart.distanceTo(e.latlng);
                    var distKm = (dist / 1000).toFixed(2);
                    var distMi = (dist / 1609.34).toFixed(2);
                    if (measureLine) map.removeLayer(measureLine);
                    measureLine = L.polyline([measureStart, e.latlng], { color: '#FF8833', weight: 2, dashArray: '6,4' }).addTo(map);
                    L.popup().setLatLng(e.latlng)
                        .setContent('<div style="font-family:Consolas;font-size:10px;color:#FF8833;padding:2px">&#x1F4CF; ' + distKm + ' km / ' + distMi + ' mi</div>')
                        .openOn(map);
                    measureStart = null;
                }
            });
        }

        function switchView(view) {
            currentView = view;
            document.querySelectorAll('.toolbar button').forEach(function(b) { b.classList.remove('active'); });

            if (view === 'sat') {
                document.getElementById('btnSat').classList.add('active');
                initSatView();
            } else if (view === 'street') {
                document.getElementById('btnStreet').classList.add('active');
                document.getElementById('viewer').innerHTML =
                    '<iframe src="https://www.google.com/maps/embed?pb=!4v0!6m8!1m7!1s0!2m2!1d' + currentLat + '!2d' + currentLon +
                    '!3f0!4f0!5f0.7820865974627469" style="width:100%;height:100%;border:0" allowfullscreen loading="lazy"></iframe>' +
                    '<div style="position:absolute;top:50px;left:50%;transform:translateX(-50%);background:#080810ee;border:1px solid #33CC33;' +
                    'border-radius:4px;padding:8px 16px;font-family:Consolas;font-size:11px;color:#33CC33;z-index:1000">' +
                    '&#x1F4F7; Street View &#x2014; If unavailable, try the source links panel &#x2192;</div>';
            } else if (view === 'webcam') {
                document.getElementById('btnWebcam').classList.add('active');
                document.getElementById('viewer').innerHTML =
                    '<iframe src="https://www.windy.com/webcams/map/__LAT__,__LON__,12" ' +
                    'style="width:100%;height:100%;border:0" allowfullscreen></iframe>' +
                    '<div style="position:absolute;top:50px;left:50%;transform:translateX(-50%);background:#080810ee;border:1px solid #AA55FF;' +
                    'border-radius:4px;padding:8px 16px;font-family:Consolas;font-size:11px;color:#AA55FF;z-index:1000">' +
                    '&#x1F4F9; Nearby Webcams &#x2014; Powered by Windy.com</div>';
            }
        }

        function toggleNV() {
            nvActive = !nvActive;
            document.getElementById('nvOverlay').style.display = nvActive ? 'block' : 'none';
            document.getElementById('nvScanline').style.display = nvActive ? 'block' : 'none';
            document.getElementById('btnNV').classList.toggle('active', nvActive);
            if (nvActive && map) {
                document.getElementById('viewer').style.filter = 'saturate(0) brightness(0.8) sepia(1) hue-rotate(70deg) saturate(3) brightness(1.2)';
            } else {
                document.getElementById('viewer').style.filter = 'none';
            }
        }

        initSatView();
    </script>
</body>
</html>
"""
                .Replace("__LAT__", latStr)
                .Replace("__LON__", lonStr)
                .Replace("__LOCNAME__", escapedName);
        }
    }

    // ═══════════════════════════════════════════
    //  VIEW MODELS
    // ═══════════════════════════════════════════

    public class EventViewModel
    {
        public ConflictEvent Event { get; }

        public EventViewModel(ConflictEvent e) => Event = e;

        public string Title => Event.Title;
        public string Summary => Event.Summary;
        public string Source => Event.Source;

        public string SeverityLabel => Event.Severity switch
        {
            SeverityLevel.Critical => "CRIT",
            SeverityLevel.High => "HIGH",
            SeverityLevel.Medium => "MED",
            _ => "LOW"
        };

        public string SeverityColor => Event.Severity switch
        {
            SeverityLevel.Critical => "#EE3333",
            SeverityLevel.High => "#FF8833",
            SeverityLevel.Medium => "#3388FF",
            _ => "#6A6A80"
        };

        public string CategoryIcon => Event.Category switch
        {
            EventCategory.Military => "⚔",
            EventCategory.Diplomatic => "🏛",
            EventCategory.Economic => "💰",
            EventCategory.Nuclear => "☢",
            EventCategory.Cyber => "💻",
            EventCategory.Humanitarian => "🏥",
            EventCategory.Intelligence => "🕵",
            _ => "📡"
        };

        public string TimeAgo
        {
            get
            {
                var diff = DateTime.UtcNow - Event.Timestamp;
                if (diff.TotalMinutes < 1) return "just now";
                if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
                return $"{(int)diff.TotalDays}d ago";
            }
        }

        public string TagsDisplay => Event.Tags.Count > 0
            ? string.Join(" ", Event.Tags.Select(t => $"[{t}]"))
            : "";
    }

    public class AlertRuleViewModel
    {
        private readonly AlertRule _rule;
        public AlertRuleViewModel(AlertRule r) => _rule = r;

        public string Name => _rule.Name;
        public string StatusText => _rule.Enabled ? "● ACTIVE" : "○ DISABLED";
        public string StatusColor => _rule.Enabled ? "#33CC33" : "#6A6A80";
        public string Description =>
            $"Min severity: {_rule.MinSeverity} | Keywords: {string.Join(", ", _rule.Keywords.Take(5))}" +
            (_rule.Keywords.Count > 5 ? $" +{_rule.Keywords.Count - 5} more" : "");
    }

    public class EclipseViewModel
    {
        public EclipseEvent Eclipse { get; }

        public EclipseViewModel(EclipseEvent e) => Eclipse = e;

        public string Name => Eclipse.Name;
        public string Description => Eclipse.Description;
        public string DateDisplay => Eclipse.Date.ToString("yyyy-MM-dd HH:mm UTC");

        public string Icon => Eclipse.Type switch
        {
            EclipseType.SolarTotal => "🌑",
            EclipseType.SolarAnnular => "🌗",
            EclipseType.SolarPartial => "🌘",
            EclipseType.LunarTotal => "🌕",
            EclipseType.LunarPartial => "🌔",
            _ => "🌙"
        };

        public string StatusColor
        {
            get
            {
                if (Eclipse.Date < DateTime.UtcNow) return "#6A6A80";
                if (Eclipse.MECoveragePercent >= 70) return "#EE3333";
                if (Eclipse.MECoveragePercent >= 40) return "#FF8833";
                if (Eclipse.IsVisibleFromME) return "#3388FF";
                return "#6A6A80";
            }
        }

        public string MELabel => Eclipse.IsVisibleFromME
            ? $"ME: {Eclipse.MECoveragePercent}%"
            : "NOT VISIBLE";

        public string MELabelColor => Eclipse.IsVisibleFromME ? "#33CC33" : "#6A6A80";

        public string CountdownDisplay
        {
            get
            {
                if (Eclipse.Date < DateTime.UtcNow) return "PAST";
                var diff = Eclipse.Date - DateTime.UtcNow;
                if (diff.TotalDays > 365) return $"T-{diff.Days / 365}y {diff.Days % 365}d";
                if (diff.TotalDays > 30) return $"T-{diff.Days}d";
                return $"T-{diff.Days}d {diff.Hours}h";
            }
        }

        public string MagnitudeDisplay => $"Mag: {Eclipse.MaxMagnitude:F3}";
    }

    public class ForceViewModel
    {
        public ForceComposition Force { get; }

        public ForceViewModel(ForceComposition f) => Force = f;

        public string Country => Force.Country;
        public string FlagEmoji => Force.FlagEmoji;
        public string DisplayName => $"{Force.FlagEmoji} {Force.Country}";
        public string PersonnelDisplay => $"{Force.ActivePersonnel:N0} active | {Force.ReservePersonnel:N0} reserve";
        public string BudgetDisplay => $"${Force.DefenseBudgetBillions:F1}B";

        public string EquipmentSummary
        {
            get
            {
                var aircraft = Force.Equipment.Where(e => e.Domain == EquipmentDomain.AirForce).Sum(e => e.QuantityActive);
                var drones = Force.Equipment.Where(e => e.Domain == EquipmentDomain.Drones).Sum(e => e.QuantityActive);
                var naval = Force.Equipment.Where(e => e.Domain == EquipmentDomain.Navy).Sum(e => e.QuantityActive);
                var ground = Force.Equipment.Where(e => e.Domain == EquipmentDomain.GroundForces).Sum(e => e.QuantityActive);
                var parts = new List<string>();
                if (aircraft > 0) parts.Add($"✈ {aircraft}");
                if (drones > 0) parts.Add($"🛩 {drones}");
                if (naval > 0) parts.Add($"🚢 {naval}");
                if (ground > 0) parts.Add($"🪖 {ground}");
                return string.Join(" │ ", parts);
            }
        }

        public string Notes => Force.Notes;
        public int EquipmentCount => Force.Equipment.Count;

        public string RankDisplay => Force.GlobalFirepowerRank > 0
            ? $"GFP #{Force.GlobalFirepowerRank}"
            : "N/A";

        public string RankColor => Force.GlobalFirepowerRank switch
        {
            <= 0 => "#6A6A80",
            <= 5 => "#EE3333",
            <= 15 => "#FF8833",
            <= 30 => "#DDCC33",
            _ => "#33CC33"
        };
    }
}
