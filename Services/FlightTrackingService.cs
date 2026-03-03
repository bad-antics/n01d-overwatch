using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using N01D.Overwatch.Models;

namespace N01D.Overwatch.Services
{
    /// <summary>
    /// Fetches military and notable aircraft positions from the OpenSky Network API (free, no key required).
    /// Monitors a bounding box covering the Middle East region.
    /// </summary>
    public class FlightTrackingService
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(25) };

        // Middle East bounding box: lat 12-42, lon 24-64
        private const double LatMin = 12.0, LatMax = 42.0, LonMin = 24.0, LonMax = 64.0;
        private const string OpenSkyUrl = "https://opensky-network.org/api/states/all";

        // ── Caching + Rate-Limit Protection ──
        private List<FlightData> _cachedFlights = new();
        private DateTime _lastSuccessfulFetch = DateTime.MinValue;
        private DateTime _lastApiCall = DateTime.MinValue;
        private int _consecutiveFailures = 0;
        private bool _isRateLimited = false;
        private DateTime _rateLimitedUntil = DateTime.MinValue;
        private const int MinPollIntervalSeconds = 15;  // Don't hit API more than once per 15s
        private const int MaxBackoffSeconds = 300;       // Max 5-minute backoff on repeated failures

        /// <summary>Status message describing last API result — for UI display.</summary>
        public string LastStatus { get; private set; } = "Awaiting first scan...";
        /// <summary>Whether the last call was rate-limited (429).</summary>
        public bool IsRateLimited => _isRateLimited;
        /// <summary>Age of cached data.</summary>
        public TimeSpan CacheAge => _lastSuccessfulFetch == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.UtcNow - _lastSuccessfulFetch;

        // Known military callsign prefixes — comprehensive list
        private static readonly string[] _milPrefixes = {
            // ── US Military ──
            "RCH", "REACH",       // AMC strategic airlift
            "DUKE",               // Special operations
            "EVAC",               // Aeromedical evacuation
            "BOLT",               // Fighter deployments
            "VIPER",              // F-16 ops
            "HAWK",               // Various fighter
            "FURY",               // Strike missions
            "FORTE",              // RQ-4 Global Hawk ISR
            "DRACO",              // ISR/Recon
            "IRON",               // Iron Hand SEAD
            "SNTRY", "AWACS",     // E-3 Sentry AWACS
            "MAGIC",              // E-3 AWACS callsign
            "DRAGN",              // U-2 Dragon Lady
            "NCHO", "TOPCT",      // Special ops
            "VADER",              // F-22 Raptor
            "CONVOY",             // Ground logistics air
            "ATLAS", "REACH",     // C-17 Globemaster III
            "GIANT",              // C-5 Galaxy
            "HOMER",              // E-6B Mercury (nuclear C2)
            "GORDO",              // KC-135 Stratotanker
            "SHELL", "TEAL",      // Aerial refueling
            "ARCT",               // KC-46A Pegasus
            "STEEL",              // KC-10 Extender
            "NEON",               // EC-130H Compass Call EW
            "JSTAR",              // E-8 JSTARS ground surveil
            "RAIDR", "DEATH",     // B-2 Spirit
            "BONE",               // B-1B Lancer
            "DOOM", "GRIM",       // MQ-9 Reaper
            "REAPER",             // MQ-9 Reaper
            "KING",               // HC-130J rescue
            "PEDRO", "JOLLY",     // HH-60 Pave Hawk CSAR
            "SLIP", "BLADE",      // CV-22 Osprey
            "NIGHT",              // AC-130 gunship
            "SPOOK",              // AC-130J Ghostrider
            "HAVE",               // Classified programs
            "COBRA",              // AH-1 Cobra / ops
            "TIGER",              // Fighter mixed
            "RAPTOR",             // F-22
            "STRIKE",             // Strike package
            "WEASEL",             // SEAD/DEAD
            "THUD",               // Wild Weasel heritage
            "CYLON",              // RQ-170 Sentinel
            "BAT",                // Stealth ops
            "SAM",                // SAR missions
            "CANOE",              // Navy P-8 Poseidon
            "TRIDENT",            // Navy operations
            "RED",                // Red Flag exercises
            "BULL",               // MV-22 / USMC
            "RAIDER",             // USMC ops
            "VMFA",               // USMC fighter attack
            "VMGR",               // USMC refueler/transport
            "HMH", "HMM", "HML", // USMC helo squadrons
            "MARLN",              // P-8A Poseidon maritime patrol
            "LMTR",               // Navy P-8 / EP-3
            "MPRA",               // Maritime patrol recon

            // ── US Special / Strategic ──
            "THORN",              // Special operations
            "KNIFE",              // SOF direct action
            "WRATH",              // Strike ops
            "ORDER", "ORDEN",     // STRATCOM
            "SKY",                // E-4B Nightwatch (Doomsday)
            "NAVY",               // USN misc
            "NAVY",               // USN callsign block

            // ── Iranian Military ──
            "IAF", "IRIAF", "IRGC", "IIAF",
            "IRI",                // Islamic Republic of Iran
            "EP", "EPA",          // Iran Air Force prefix
            "IRG",                // IRGC Air
            "QFZ",                // Quds Force

            // ── Israeli Military ──
            "IDAL",               // Israeli AF
            "ISR",                // Israeli military

            // ── UK Military ──
            "RRR", "RAF",         // Royal Air Force
            "ASCOT",              // RAF transport
            "TARTAN",             // RAF Typhoon
            "LOSSIE",             // RAF Lossiemouth
            "TYPHON",             // RAF Typhoon

            // ── French Military ──
            "FAF", "CTM",         // French Air Force
            "RAFALE",             // Rafale ops
            "COTAM",              // French mil transport

            // ── Other NATO / Coalition ──
            "GAF",                // German Air Force
            "BAF",                // Belgian AF
            "CNV",                // French Navy
            "ITAL",               // Italian AF
            "MMF",                // NATO Multinational MRTT
            "NATO",               // NATO AWACS

            // ── Gulf States──
            "CAMEL",              // Gulf state ops
            "UAE", "UAEAF",       // UAE Air Force
            "QAF",                // Qatar Air Force
            "RSAF",               // Royal Saudi AF
            "SAF",                // Saudi AF
            "BAAH",               // Bahrain AF
            "KAF",                // Kuwaiti AF
            "OMAN",               // Royal Air Force of Oman
            "JOAF",               // Jordanian AF

            // ── Other Regional ──
            "TRK", "THK",         // Turkish AF
            "EGY",                // Egyptian AF
            "PAF",                // Pakistan AF
            "INDIAN",             // Indian AF

            // ── Additional US Callsigns ──
            "DECOY",              // Decoy/countermeasures
            "FLASH",              // Fast-movers
            "GHOST",              // Stealth ops
            "LANCE",              // Heavy attack
            "ORCA",               // Submarine hunters
            "RAVEN",              // ISR/EW
            "SHADOW",             // SOF helo
            "SPECTRE",            // AC-130
            "STALKER",            // Persistent ISR
            "STORM",              // CAS operations
            "TALON",              // SOF
            "VENOM",              // AH-1Z
            "WIDOW",              // AH-64 Apache
            "BANDIT",             // Fighter intercept
            "HUNTER",             // Hunter-killer ops
            "SABER",              // Cavalry scout
            "SPARTAN",            // C-27J
            "DRAGONFLY",          // A-10 CAS
            "HOG",                // A-10 Warthog
            "WARDOG",             // Mixed fighter
            "WOLFPACK",           // F-16 package
            "PHOENIX",            // F-15 ops
            "EAGLE",              // F-15 Eagle
            "MUSTANG",            // Heritage/F-16
            "ROGUE",              // Aggressor squadron
            "AGGRESSOR",          // Red air
            "TOPGUN",             // Navy aggressor

            // ── Russian Military ──
            "RFF", "RSD",         // Russian AF
            "CCCP",               // Legacy Soviet
            "SU", "MIG", "TU",   // Aircraft type prefixes
            "ANTONOV",            // Heavy transport

            // ── Chinese Military ──
            "CFC", "PLAAF",       // PLA Air Force
            "PLAN",               // PLA Navy

            // ── Drone / UAS Callsigns ──  
            "PRED",               // MQ-1 Predator
            "SCAN",               // Scan Eagle
            "GRAY",               // Gray Eagle MQ-1C
            "FIDO",               // Fire Scout
            "TRITON",             // MQ-4C Triton
            "SENTINEL",           // RQ-170
            "DARK",               // Classified UAS
            "ORBITER",            // Israeli Orbiter
            "HERMES",             // Israeli Hermes 900
            "HERON",              // IAI Heron
            "HAROP",              // IAI Harop loitering
            "WING",               // Wing Loong (Chinese)
            "CH",                 // CAIG CH-series drones
            "MOHAJER",            // Iranian Mohajer
            "SHAHED",             // Iranian Shahed
            "ABABIL",             // Iranian Ababil
            "KARRAR",             // Iranian Karrar UCAV
            "KAMAN",              // Iranian Kaman-22
            "AKINCI",             // Turkish Bayraktar Akıncı
            "TB2",                // Turkish Bayraktar TB2

            // ── Tanker / ISR Extended ──
            "CASA",               // EADS CASA transport
            "VOYAGER",            // RAF A330 MRTT
            "ASTRA",              // ISR platform
            "POSEIDON",           // P-8A alternate
            "MERLIN",             // UK/NATO helo
            "WILDCAT",            // AW159 Wildcat
            "CHINOOK",            // CH-47
            "OSPREY",             // V-22/MV-22
        };

        // Known military aircraft ICAO24 hex ranges
        private static readonly (long Start, long End)[] _milHexRanges = {
            // US military
            (0xADF7C7, 0xAFFFFF), // USAF
            (0xA00001, 0xADF7C6), // US Army / other DoD
            // UK military
            (0x43C000, 0x43CFFF), // RAF
            // France military  
            (0x3B0000, 0x3BFFFF), // French military
            // Israel military
            (0x738000, 0x738FFF), // IAF
            // Iran military
            (0x730000, 0x730FFF), // IRIAF partial range
            // Turkey military
            (0x4B8000, 0x4B8FFF), // THK
            // Saudi military
            (0x710000, 0x710FFF), // RSAF partial
            // UAE military
            (0x896000, 0x896FFF), // UAEAF partial
        };

        public async Task<List<FlightData>> FetchMilitaryFlightsAsync()
        {
            // ── Rate-limit / backoff guard ──
            var now = DateTime.UtcNow;
            var sinceLastCall = (now - _lastApiCall).TotalSeconds;

            // Enforce minimum poll interval
            if (sinceLastCall < MinPollIntervalSeconds)
            {
                LastStatus = _cachedFlights.Count > 0
                    ? $"Using cache ({_cachedFlights.Count} aircraft, {CacheAge.TotalSeconds:N0}s old) — cooldown"
                    : "Cooldown — waiting to poll API";
                return _cachedFlights;
            }

            // Exponential backoff on repeated failures
            if (_isRateLimited && now < _rateLimitedUntil)
            {
                var wait = (_rateLimitedUntil - now).TotalSeconds;
                LastStatus = _cachedFlights.Count > 0
                    ? $"Rate-limited — using cache ({_cachedFlights.Count} aircraft, {CacheAge.TotalSeconds:N0}s old) — retry in {wait:N0}s"
                    : $"Rate-limited — retry in {wait:N0}s";
                return _cachedFlights;
            }

            _lastApiCall = now;
            var flights = new List<FlightData>();

            try
            {
                // Try full bounding box first
                flights = await FetchFromOpenSkyAsync(LatMin, LonMin, LatMax, LonMax);

                // Success — reset failure tracking
                _consecutiveFailures = 0;
                _isRateLimited = false;
                _cachedFlights = flights;
                _lastSuccessfulFetch = DateTime.UtcNow;
                LastStatus = $"{flights.Count} military aircraft detected (live)";
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // 429 — rate limited
                _consecutiveFailures++;
                _isRateLimited = true;
                var backoff = Math.Min(MinPollIntervalSeconds * Math.Pow(2, _consecutiveFailures), MaxBackoffSeconds);
                _rateLimitedUntil = DateTime.UtcNow.AddSeconds(backoff);
                LastStatus = _cachedFlights.Count > 0
                    ? $"API rate-limited (429) — showing cache ({_cachedFlights.Count} aircraft, {CacheAge.TotalSeconds:N0}s old) — backoff {backoff:N0}s"
                    : $"API rate-limited (429) — no cache yet — backoff {backoff:N0}s";
                return _cachedFlights;
            }
            catch (Exception ex)
            {
                // Other errors (timeout, network, etc.)
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3)
                {
                    var backoff = Math.Min(30 * Math.Pow(2, _consecutiveFailures - 3), MaxBackoffSeconds);
                    _rateLimitedUntil = DateTime.UtcNow.AddSeconds(backoff);
                    _isRateLimited = true;
                }
                LastStatus = _cachedFlights.Count > 0
                    ? $"API error ({ex.Message.Split('\n')[0]}) — showing cache ({_cachedFlights.Count} aircraft)"
                    : $"API error: {ex.Message.Split('\n')[0]}";
                return _cachedFlights;
            }

            return flights;
        }

        /// <summary>
        /// Raw OpenSky fetch for a specific bounding box. Throws on HTTP errors.
        /// </summary>
        private async Task<List<FlightData>> FetchFromOpenSkyAsync(double latMin, double lonMin, double latMax, double lonMax)
        {
            var flights = new List<FlightData>();
            var url = $"{OpenSkyUrl}?lamin={latMin}&lomin={lonMin}&lamax={latMax}&lomax={lonMax}";

            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array)
                return flights;

            foreach (var s in states.EnumerateArray())
            {
                if (s.GetArrayLength() < 17) continue;

                var icao24 = s[0].GetString() ?? "";
                var callsign = (s[1].GetString() ?? "").Trim();
                var country = s[2].GetString() ?? "";
                var lon = s[5].ValueKind == JsonValueKind.Number ? s[5].GetDouble() : (double?)null;
                var lat = s[6].ValueKind == JsonValueKind.Number ? s[6].GetDouble() : (double?)null;
                var alt = s[7].ValueKind == JsonValueKind.Number ? s[7].GetDouble() : 0.0;
                var speed = s[9].ValueKind == JsonValueKind.Number ? s[9].GetDouble() : 0.0;
                var heading = s[10].ValueKind == JsonValueKind.Number ? s[10].GetDouble() : 0.0;

                if (lat == null || lon == null) continue;

                bool isMil = IsMilitaryCallsign(callsign) || IsMilitaryHex(icao24) ||
                             IsMilitaryCountryOfInterest(country);

                if (!isMil) continue;

                flights.Add(new FlightData
                {
                    Callsign = callsign,
                    Registration = icao24,
                    AircraftType = GuessAircraftType(callsign),
                    Country = country,
                    Latitude = lat.Value,
                    Longitude = lon.Value,
                    Altitude = alt,
                    Speed = speed * 1.944, // m/s → knots
                    Heading = heading,
                    IsMilitary = true,
                    LastSeen = DateTime.UtcNow
                });
            }

            return flights;
        }

        private static bool IsMilitaryCallsign(string cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return false;
            var upper = cs.ToUpperInvariant();
            return _milPrefixes.Any(p => upper.StartsWith(p));
        }

        private static bool IsMilitaryHex(string icao24)
        {
            if (string.IsNullOrWhiteSpace(icao24)) return false;
            if (long.TryParse(icao24, System.Globalization.NumberStyles.HexNumber, null, out var hex))
                return _milHexRanges.Any(r => hex >= r.Start && hex <= r.End);
            return false;
        }

        private static bool IsMilitaryCountryOfInterest(string country)
        {
            var milCountries = new[] {
                "Iran", "Israel", "United States",
                "United Kingdom", "France", "Turkey",
                "Saudi Arabia", "United Arab Emirates",
                "Russian Federation"
            };
            return milCountries.Any(c => country.Contains(c, StringComparison.OrdinalIgnoreCase));
        }

        private static string GuessAircraftType(string callsign)
        {
            var cs = callsign.ToUpperInvariant();
            // ISR / Recon
            if (cs.StartsWith("FORTE")) return "RQ-4 Global Hawk (ISR)";
            if (cs.StartsWith("DRAGN")) return "U-2 Dragon Lady (ISR)";
            if (cs.StartsWith("CYLON")) return "RQ-170 Sentinel (Stealth ISR)";
            if (cs.StartsWith("DOOM") || cs.StartsWith("GRIM") || cs.StartsWith("REAPER")) return "MQ-9 Reaper (Armed ISR)";
            if (cs.StartsWith("CANOE") || cs.StartsWith("MARLN") || cs.StartsWith("MPRA")) return "P-8A Poseidon (Maritime Patrol)";
            if (cs.StartsWith("LMTR")) return "EP-3E Aries II (SIGINT)";
            // AWACS / C2 / Strategic
            if (cs.StartsWith("SNTRY") || cs.StartsWith("AWACS") || cs.StartsWith("MAGIC")) return "E-3 Sentry (AWACS)";
            if (cs.StartsWith("JSTAR")) return "E-8C JSTARS (Ground Surveillance)";
            if (cs.StartsWith("HOMER")) return "E-6B Mercury (Nuclear C2)";
            if (cs.StartsWith("SKY")) return "E-4B Nightwatch (Doomsday Plane)";
            if (cs.StartsWith("NEON")) return "EC-130H Compass Call (EW)";
            // Bombers
            if (cs.StartsWith("RAIDR") || cs.StartsWith("DEATH") || cs.StartsWith("BAT")) return "B-2 Spirit (Stealth Bomber)";
            if (cs.StartsWith("BONE")) return "B-1B Lancer (Bomber)";
            // Fighters
            if (cs.StartsWith("VADER") || cs.StartsWith("RAPTOR")) return "F-22 Raptor";
            if (cs.StartsWith("BOLT") || cs.StartsWith("VIPER")) return "F-16 Viper";
            if (cs.StartsWith("STRIKE")) return "F-15E Strike Eagle";
            if (cs.StartsWith("HAWK")) return "Fighter (Type Unknown)";
            if (cs.StartsWith("WEASEL") || cs.StartsWith("THUD")) return "F-16CJ (SEAD/DEAD)";
            if (cs.StartsWith("RAFALE")) return "Rafale (French AF)";
            if (cs.StartsWith("TYPHON") || cs.StartsWith("TARTAN")) return "Eurofighter Typhoon";
            // Gunship
            if (cs.StartsWith("NIGHT") || cs.StartsWith("SPOOK")) return "AC-130J Ghostrider (Gunship)";
            // Transport / Airlift
            if (cs.StartsWith("RCH") || cs.StartsWith("REACH") || cs.StartsWith("ATLAS")) return "C-17 Globemaster III";
            if (cs.StartsWith("GIANT")) return "C-5M Super Galaxy";
            if (cs.StartsWith("EVAC")) return "C-130J Hercules";
            if (cs.StartsWith("ASCOT") || cs.StartsWith("COTAM")) return "Military Transport";
            // Tankers
            if (cs.StartsWith("GORDO") || cs.StartsWith("SHEL") || cs.StartsWith("TEAL")) return "KC-135 Stratotanker";
            if (cs.StartsWith("STEEL")) return "KC-10 Extender";
            if (cs.StartsWith("ARCT")) return "KC-46A Pegasus";
            // Rotary / SOF
            if (cs.StartsWith("PEDRO") || cs.StartsWith("JOLLY")) return "HH-60 Pave Hawk (CSAR)";
            if (cs.StartsWith("KING")) return "HC-130J (Combat Rescue)";
            if (cs.StartsWith("SLIP") || cs.StartsWith("BLADE")) return "CV-22 Osprey (SOF)";
            if (cs.StartsWith("DUKE") || cs.StartsWith("THORN") || cs.StartsWith("KNIFE")) return "SOF Aircraft";
            if (cs.StartsWith("COBRA")) return "Attack Helicopter";
            // Misc
            if (cs.StartsWith("BULL") || cs.StartsWith("RAIDER")) return "USMC Aviation";
            if (cs.StartsWith("TRIDENT") || cs.StartsWith("NAVY") || cs.StartsWith("NAVY")) return "USN Aviation";
            if (cs.StartsWith("NATO")) return "NATO AWACS (E-3A)";
            if (cs.StartsWith("MMF")) return "A330 MRTT (NATO Tanker)";
            // Drones / UAS
            if (cs.StartsWith("PRED")) return "MQ-1 Predator (ISR)";
            if (cs.StartsWith("GRAY")) return "MQ-1C Gray Eagle (ISR)";
            if (cs.StartsWith("TRITON")) return "MQ-4C Triton (Maritime ISR)";
            if (cs.StartsWith("SENTINEL")) return "RQ-170 Sentinel (Stealth ISR)";
            if (cs.StartsWith("SCAN")) return "ScanEagle (Tactical ISR)";
            if (cs.StartsWith("HERMES") || cs.StartsWith("HERON")) return "IAI Heron/Hermes (Israeli ISR)";
            if (cs.StartsWith("HAROP")) return "IAI Harop (Loitering Munition)";
            if (cs.StartsWith("ORBITER")) return "Aeronautics Orbiter (Israeli ISR)";
            if (cs.StartsWith("MOHAJER")) return "Mohajer-6 (Iranian UCAV)";
            if (cs.StartsWith("SHAHED")) return "Shahed-136/129 (Iranian Drone)";
            if (cs.StartsWith("ABABIL")) return "Ababil-3 (Iranian Drone)";
            if (cs.StartsWith("KARRAR")) return "Karrar (Iranian UCAV)";
            if (cs.StartsWith("KAMAN")) return "Kaman-22 (Iranian Drone)";
            if (cs.StartsWith("AKINCI")) return "Bayraktar Akıncı (Turkish UCAV)";
            if (cs.StartsWith("TB2")) return "Bayraktar TB2 (Turkish UCAV)";
            if (cs.StartsWith("WING")) return "Wing Loong II (Chinese UCAV)";
            // CAS / Attack
            if (cs.StartsWith("HOG") || cs.StartsWith("DRAGONFLY")) return "A-10 Thunderbolt II (CAS)";
            if (cs.StartsWith("WIDOW")) return "AH-64 Apache (Attack Helo)";
            if (cs.StartsWith("VENOM")) return "AH-1Z Viper (Attack Helo)";
            if (cs.StartsWith("SPECTRE")) return "AC-130 Spectre (Gunship)";
            // Fighters extended
            if (cs.StartsWith("EAGLE") || cs.StartsWith("PHOENIX")) return "F-15 Eagle";
            if (cs.StartsWith("WOLFPACK") || cs.StartsWith("MUSTANG")) return "F-16 Fighting Falcon";
            if (cs.StartsWith("GHOST") || cs.StartsWith("BAT")) return "Stealth Aircraft";
            if (cs.StartsWith("BANDIT") || cs.StartsWith("WARDOG")) return "Fighter (Intercept)";
            if (cs.StartsWith("ROGUE") || cs.StartsWith("AGGRESSOR") || cs.StartsWith("TOPGUN")) return "Aggressor Squadron";
            // SOF / Special
            if (cs.StartsWith("SHADOW") || cs.StartsWith("TALON")) return "SOF Rotary Wing";
            if (cs.StartsWith("STALKER") || cs.StartsWith("RAVEN")) return "ISR Platform";
            if (cs.StartsWith("HUNTER") || cs.StartsWith("SABER")) return "Hunter-Killer UAS/Helo";
            // Transport extended
            if (cs.StartsWith("SPARTAN")) return "C-27J Spartan";
            if (cs.StartsWith("CHINOOK")) return "CH-47 Chinook";
            if (cs.StartsWith("OSPREY")) return "V-22/MV-22 Osprey";
            if (cs.StartsWith("VOYAGER")) return "A330 MRTT Voyager";
            // Russian
            if (cs.StartsWith("ANTONOV")) return "An-124 Ruslan (Heavy Transport)";
            if (cs.StartsWith("SU")) return "Sukhoi Fighter";
            if (cs.StartsWith("MIG")) return "MiG Fighter";
            if (cs.StartsWith("TU")) return "Tupolev (Bomber/ISR)";
            return "Military Aircraft";
        }

        /// <summary>
        /// Classifies how significant a tracked aircraft is — high-value assets get elevated severity.
        /// </summary>
        public static SeverityLevel ClassifyFlightSeverity(string callsign, string aircraftType)
        {
            var cs = callsign.ToUpperInvariant();
            // Critical — strategic / nuclear C2 / stealth
            if (cs.StartsWith("HOMER") || cs.StartsWith("SKY") ||
                cs.StartsWith("RAIDR") || cs.StartsWith("DEATH") ||
                cs.StartsWith("ORDER") || cs.StartsWith("GHOST") ||
                cs.StartsWith("HAROP") || cs.StartsWith("SHAHED"))
                return SeverityLevel.Critical;
            // High — bombers, gunships, ISR drones, SEAD, loitering munitions
            if (cs.StartsWith("BONE") || cs.StartsWith("FORTE") || cs.StartsWith("DRAGN") ||
                cs.StartsWith("CYLON") || cs.StartsWith("NIGHT") || cs.StartsWith("SPOOK") ||
                cs.StartsWith("WEASEL") || cs.StartsWith("DOOM") || cs.StartsWith("GRIM") ||
                cs.StartsWith("REAPER") || cs.StartsWith("JSTAR") || cs.StartsWith("NEON") ||
                cs.StartsWith("VADER") || cs.StartsWith("RAPTOR") ||
                cs.StartsWith("SPECTRE") || cs.StartsWith("STALKER") ||
                cs.StartsWith("WIDOW") || cs.StartsWith("HOG") || cs.StartsWith("DRAGONFLY") ||
                cs.StartsWith("AKINCI") || cs.StartsWith("MOHAJER") || cs.StartsWith("KARRAR") ||
                cs.StartsWith("TRITON") || cs.StartsWith("SENTINEL") ||
                aircraftType.Contains("Stealth") || aircraftType.Contains("Bomber") ||
                aircraftType.Contains("Gunship") || aircraftType.Contains("SEAD") ||
                aircraftType.Contains("Loitering") || aircraftType.Contains("UCAV"))
                return SeverityLevel.High;
            // Medium — fighters, patrol, tankers, AWACS
            if (cs.StartsWith("SNTRY") || cs.StartsWith("AWACS") || cs.StartsWith("MAGIC") ||
                cs.StartsWith("CANOE") || cs.StartsWith("MARLN") ||
                cs.StartsWith("STRIKE") || cs.StartsWith("BOLT") || cs.StartsWith("VIPER") ||
                cs.StartsWith("GORDO") || cs.StartsWith("STEEL") || cs.StartsWith("ARCT"))
                return SeverityLevel.Medium;
            return SeverityLevel.Low;
        }

        /// <summary>Convenience overload accepting a FlightData object.</summary>
        public static SeverityLevel ClassifyFlightSeverity(FlightData f) =>
            ClassifyFlightSeverity(f.Callsign, f.AircraftType);

        public ConflictEvent? ToConflictEvent(FlightData flight)
        {
            if (!flight.IsMilitary) return null;
            var severity = ClassifyFlightSeverity(flight.Callsign, flight.AircraftType);
            var icon = severity switch
            {
                SeverityLevel.Critical => "🔴✈",
                SeverityLevel.High => "🟠✈",
                SeverityLevel.Medium => "🟡✈",
                _ => "✈"
            };
            return new ConflictEvent
            {
                Title = $"{icon} {flight.Callsign} — {flight.AircraftType}",
                Summary = $"{flight.Country} | Alt: {flight.Altitude:N0}m | Speed: {flight.Speed:N0}kts | Heading: {flight.Heading:N0}° | ICAO: {flight.Registration}",
                Source = "OpenSky Network",
                DataSource = DataSource.FlightTracker,
                Category = EventCategory.Military,
                Severity = severity,
                Latitude = flight.Latitude,
                Longitude = flight.Longitude,
                Location = $"{flight.Latitude:F2}, {flight.Longitude:F2}",
                Timestamp = flight.LastSeen,
                Tags = new List<string> { "FLIGHT", flight.Country.ToUpperInvariant().Split(',')[0], flight.AircraftType.Split(' ')[0].ToUpperInvariant() }
            };
        }
    }
}
