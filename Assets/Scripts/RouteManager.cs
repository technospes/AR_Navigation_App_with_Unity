using System;
using Mapbox.Directions;
using Mapbox.Unity;
using Mapbox.Unity.Location;
using Mapbox.Unity.Map;
using Mapbox.Unity.Utilities;
using Mapbox.Utils;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using System.Linq;
using Mapbox.MapMatching;
using System.Globalization;
using UnityEngine.UIElements;
using System.Text;


#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class RouteManager : MonoBehaviour
{
    [Header("Dynamic Spawning")]
    public float spawnAheadDistance = 100f;  // Meters ahead to spawn
    public float despawnBehindDistance = 20f;  // Meters behind to despawn
    public float updateDistanceThreshold = 10f;  // Move this far to update
    private Vector3 lastUserPositionForUpdate;  // Track user pos
    private int lastClosestRouteIndex = 0;  // Track progress
    private List<GameObject> managedArrowObjects = new List<GameObject>();  // Track spawned arrows
    [Header("Required Components")]
    [SerializeField] private AbstractMap map;
    public GameObject arrowPrefab;
    public Transform arRouteParent;
    public OnscreenArrowLogger arrowLogger;

    [Header("AR Components")]
    public ARPlaneManager planeManager;
    public ARAnchorManager anchorManager;
    public ARRaycastManager raycastManager;
    public Camera arCamera;

    [Header("Location Services")]
    public bool useDeviceGPS = true;
    private Quaternion northAlignment = Quaternion.identity;
    private Vector2d routeOrigin;

    [Header("Arrow Orientation")]
    public float arrowYawOffsetDegrees = 0f;

    [Header("Navigation Settings")]
    public float arrowSpacing = 5f;
    public int maxArrows = 15;
    public float arrowScale = 0.5f;
    public float arrowHeightOffset = 0.1f;
    public float maxVisibleDistance = 100f;
    [Header("Navigation Behavior")]
    public float initialSpawnDistance = 100f;  // Always spawn first 100m
    public float continuousSpawnAhead = 50f;   // Keep 50m ahead spawned
    public float arrowDespawnBehind = 15f;     // Remove arrows 15m behind
    public int minimumVisibleArrows = 8;       // Always show at least 8 arrows
    public float arrowStabilityRadius = 5f;    // Prevent arrow respawning within this radius

    [Header("On-Screen Debug UI")]
    public TextMeshProUGUI apiDebugText;
    public Button testAPIButton;
    public GameObject debugPanel;

    public enum NavigationMode { Walking, Cycling, Driving }
    [Header("Route Type Selection")]
    public NavigationMode currentMode = NavigationMode.Walking;
    public int maxPointsPerSegment = 200;  // Limit per update for long routes

    // Add these private variables
    private float totalRouteDistance = 0f;
    private List<float> cumulativeDistances = new List<float>();
    [Header("Coordinate Conversion")]
    public float mapToARScale = 1f;
    public Vector3 arOriginOffset = Vector3.zero;
    public bool autoCalibrateToDeviceGPS = false;

    // FIXED: Add missing indoor scale factor
    [Header("Indoor Testing")]
    public float indoorScaleFactor = 10f; // Scale factor for indoor testing

    [Header("Route Settings")]
    public bool useTestRoute = false;
    public bool showAllArrows = true;
    public RoutingProfile routeProfile = RoutingProfile.Walking;  // Change via UI later (e.g., dropdown)
    [Header("Visual Feedback")]
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI debugText;
    public GameObject scanningIndicator;

    // Private variables
    private Directions _directions;
    private List<GameObject> activeArrows = new List<GameObject>();
    private List<Vector3> routeWorldPositions = new List<Vector3>();
    private List<Vector3> routeARPositions = new List<Vector3>();
    private List<Vector2d> rawRoutePoints = new List<Vector2d>();
    private List<int> selectedRouteIndices = new List<int>();

    // FIXED: Add missing spawnedArrows and arrowAnchors lists
    private List<GameObject> spawnedArrows = new List<GameObject>();
    private List<ARAnchor> arrowAnchors = new List<ARAnchor>();

    // GPS coordinates
    private Vector2d currentPosition = new Vector2d(29.907952863449825, 78.09645393724858);
    private Vector2d destination = new Vector2d(29.907222551756956, 78.09838079505975);
    private Mapbox.Utils.Vector2d referencePosition;

    private Vector3 arOriginWorldPos = Vector3.zero;
    private Quaternion arOriginWorldRot = Quaternion.identity;
    private bool locationInitialized = false;

    // State tracking
    private bool routeRequested = false;
    private bool routeReceived = false;
    private bool arrowsSpawned = false;
    private ARPlane primaryPlane = null;
    private bool planeLocked = false;
    private Vector3 userARPosition = Vector3.zero;

    // Debug info
    private int planesDetected = 0;
    private int raycastAttempts = 0;
    private int raycastSuccesses = 0;
    private string currentStatus = "Initializing...";

    // Ground anchoring
    private List<StaticArrowData> staticArrows = new List<StaticArrowData>();

    // FIXED: Add ARAnchorManager reference property
    private ARAnchorManager ARAnchorManagerRef => anchorManager;

    [System.Serializable]
    public class StaticArrowData
    {
        public GameObject arrowObject;
        public ARAnchor anchor;
        public Vector3 targetARPosition;
        public Vector2d gpsPosition;
        public bool isAnchored;
        public int routeIndex;
        public float distanceFromUser;

        public StaticArrowData(Vector3 arPos, Vector2d gpsPos, int index)
        {
            targetARPosition = arPos;
            gpsPosition = gpsPos;
            routeIndex = index;
            isAnchored = false;
            distanceFromUser = 0f;
        }
    }

    private void LogAR(string message)
    {
        string logMessage = $"[AR_NAV] {message}";
        Debug.Log(logMessage);
    }

    private void LogErrorAR(string message)
    {
        string logMessage = $"[AR_NAV_ERROR] {message}";
        Debug.LogError(logMessage);
    }

    #region Unity Lifecycle

    void Start()
    {
        LogAR("=== AR Navigation Starting (Mapbox v2.1.1) ===");
        RequestLocationPermission();
        LogAR($"Settings: GPS={useDeviceGPS}, TestRoute={useTestRoute}, Spacing={arrowSpacing}, Scale={mapToARScale}");
        InitializeComponents();
        lastUserPositionForUpdate = Vector3.zero;
        StartCoroutine(InitializeLocationServices());
    }

    void Update()
    {
        UpdateDebugDisplay();
        UpdateUserPosition();

        // Trigger initial route request
        if (locationInitialized && planeLocked && !routeRequested && !routeReceived)
        {
            LogAR("Ready state detected - starting route generation");
            StartCoroutine(RequestNavigationRouteWithMode());
        }

        // Dynamic arrow management - improved logic
        if (arrowsSpawned && routeARPositions.Count > 0)
        {
            float distanceMoved = Vector3.Distance(userARPosition, lastUserPositionForUpdate);

            // Update more frequently for better responsiveness
            if (distanceMoved > updateDistanceThreshold * 0.5f) // More sensitive updates
            {
                UpdateDynamicArrowSpawning();
                lastUserPositionForUpdate = userARPosition;
            }

            // Also update on time intervals for long stationary periods
            if (Time.time % 5f < Time.deltaTime) // Every 5 seconds
            {
                UpdateDynamicArrowSpawning();
            }
        }
    }

    void OnEnable()
    {
        if (planeManager != null)
            planeManager.planesChanged += OnPlanesChanged;
    }

    void OnDisable()
    {
        if (planeManager != null)
            planeManager.planesChanged -= OnPlanesChanged;
    }

    #endregion

    #region Initialization

    private void RequestLocationPermission()
    {
        LogAR("🔐 Checking GPS permissions...");
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            LogAR("📱 Requesting location permissions...");
            Permission.RequestUserPermission(Permission.FineLocation);
        }
        else
        {
            LogAR("✅ Location permissions already granted");
        }
#endif
    }

    private void InitializeComponents()
    {
        if (map == null || arrowPrefab == null || planeManager == null || raycastManager == null)
        {
            LogErrorAR("❌ Missing required components!");
            UpdateStatus("ERROR: Missing components!");
            return;
        }

        if (arCamera == null) arCamera = Camera.main;
        _directions = MapboxAccess.Instance.Directions;
        if (debugText != null) debugText.gameObject.SetActive(true);
        LogAR("✅ Components initialized");
    }

    private IEnumerator InitializeLocationServices()
    {
        LogAR("📍 Initializing location services...");
        UpdateStatus("Starting GPS...");

        if (useDeviceGPS)
        {
            if (!Input.location.isEnabledByUser)
            {
                LogAR("⚠️ GPS not enabled by user");
                UpdateStatus("GPS disabled - using preset coords");
                locationInitialized = true;
            }
            else
            {
                LogAR("🔄 Starting location service...");
                Input.location.Start(1f, 1f);

                int maxWait = 20;
                while (Input.location.status == LocationServiceStatus.Initializing && maxWait > 0)
                {
                    LogAR($"⏳ GPS initializing... {maxWait}s");
                    yield return new WaitForSeconds(1);
                    maxWait--;
                }

                if (Input.location.status == LocationServiceStatus.Running)
                {
                    var loc = Input.location.lastData;
                    currentPosition = new Vector2d(loc.latitude, loc.longitude);
                    LogAR($"✅ GPS active: {currentPosition.x:F6}, {currentPosition.y:F6}");
                    UpdateStatus("GPS acquired");
                }
                else
                {
                    LogAR($"❌ GPS failed: {Input.location.status}");
                    UpdateStatus("GPS failed - using preset coords");
                }
                locationInitialized = true;
            }
        }
        else
        {
            LogAR("📍 Using preset coordinates");
            locationInitialized = true;
        }

        if (map != null)
        {
            LogAR($"🗺️ Initializing map at: {currentPosition.x:F6}, {currentPosition.y:F6}");
            map.Initialize(currentPosition, map.AbsoluteZoom);
        }

        UpdateStatus("Point camera at ground");
        if (scanningIndicator != null) scanningIndicator.SetActive(true);
    }

    #endregion

    #region AR Plane Detection

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        planesDetected = planeManager.trackables.count;
        LogAR($"🔍 Planes changed: Added={args.added.Count}, Updated={args.updated.Count}, Removed={args.removed.Count}");
        LogAR($"📊 Total trackable planes: {planesDetected}, Locked: {planeLocked}");

        foreach (var plane in args.added)
        {
            HidePlaneCompletely(plane);
        }

        if (!planeLocked)
        {
            LogAR($"🔍 Searching for suitable plane among {planesDetected} planes...");

            foreach (var plane in planeManager.trackables)
            {
                if (plane == null) continue;

                float horizontality = Vector3.Dot(plane.transform.up, Vector3.up);
                float planeSize = plane.size.x * plane.size.y;
                bool suitable = IsSuitableForNavigation(plane);

                LogAR($"  📋 Plane: horizontal={horizontality:F2}, size={planeSize:F2}, tracking={plane.trackingState}, suitable={suitable}");

                if (suitable)
                {
                    LogAR($"🎯 FOUND SUITABLE PLANE! Locking now...");

                    primaryPlane = plane;
                    planeLocked = true;
                    arOriginWorldPos = plane.transform.position;
                    arOriginWorldRot = plane.transform.rotation;
                    referencePosition = currentPosition;

                    LogAR($"✅ PRIMARY PLANE LOCKED! Position: {arOriginWorldPos}");
                    StartCoroutine(SetupCompassAlignmentFixed());
                    UpdateStatus("Ground locked! Setting up navigation...");
                    if (scanningIndicator != null) scanningIndicator.SetActive(false);
                    break;
                }
            }

            if (!planeLocked)
            {
                LogAR($"⚠️ No suitable planes found among {planesDetected} detected planes");
                UpdateStatus($"Scanning... {planesDetected} planes found, none suitable yet");
            }
        }
        else
        {
            LogAR($"✅ Plane already locked, {planesDetected} total planes tracked");
        }
    }

    private void HidePlaneCompletely(ARPlane plane)
    {
        var childRenderers = plane.GetComponentsInChildren<Renderer>();
        foreach (var renderer in childRenderers)
        {
            renderer.enabled = false;
        }
    }

    private bool IsSuitableForNavigation(ARPlane plane)
    {
        if (plane == null) return false;

        float horizontality = Vector3.Dot(plane.transform.up, Vector3.up);
        float planeSize = plane.size.x * plane.size.y;
        bool isTracking = plane.trackingState == TrackingState.Tracking;

        bool horizontal = horizontality > 0.5f;
        bool bigEnough = planeSize > 0.5f;
        bool tracking = isTracking;

        bool suitable = horizontal && bigEnough && tracking;

        LogAR($"🔍 Plane suitability check:");
        LogAR($"   Horizontality: {horizontality:F2} (need > 0.5) = {horizontal}");
        LogAR($"   Size: {planeSize:F2} (need > 0.5) = {bigEnough}");
        LogAR($"   Tracking: {plane.trackingState} = {tracking}");
        LogAR($"   OVERALL SUITABLE: {suitable}");

        return suitable;
    }

    private IEnumerator SetupCompassAlignmentFixed()
    {
        LogAR("🧭 Setting up compass alignment...");

        if (useTestRoute)
        {
            // INDOOR MODE: Use camera direction for visualization
            LogAR("🏠 INDOOR MODE: Using camera direction for route visualization");
            northAlignment = Quaternion.Euler(0f, arCamera.transform.eulerAngles.y, 0f);
            LogAR($"📱 Using camera yaw as north: {arCamera.transform.eulerAngles.y:F1}°");
            UpdateStatus("Indoor mode - camera aligned");
        }
        else
        {
            // OUTDOOR MODE: Use real compass for accurate navigation
            LogAR("🌍 OUTDOOR MODE: Using compass for real navigation");
            Input.compass.enabled = true;

            float waitTime = 0f;
            while (waitTime < 2f)
            {
                yield return new WaitForSeconds(0.1f);
                waitTime += 0.1f;

                if (Input.compass.trueHeading != 0 && !float.IsNaN(Input.compass.trueHeading))
                {
                    LogAR($"🧭 Compass stabilized early at {waitTime:F1}s: {Input.compass.trueHeading:F1}°");
                    break;
                }
            }

            if (Input.compass.trueHeading != 0 && !float.IsNaN(Input.compass.trueHeading))
            {
                float heading = Input.compass.trueHeading;
                northAlignment = Quaternion.Euler(0f, -heading, 0f);
                LogAR($"✅ Compass ready: {heading:F1}° from true north");
                UpdateStatus($"Compass aligned: {heading:F1}°");
            }
            else
            {
                LogAR("⚠️ Compass failed, falling back to camera direction");
                northAlignment = Quaternion.Euler(0f, arCamera.transform.eulerAngles.y, 0f);
                UpdateStatus("Compass failed - using camera direction");
            }
        }

        LogAR("🚀 Starting route generation after alignment setup...");
        routeRequested = false;
        routeReceived = false;
        arrowsSpawned = false;

        rawRoutePoints.Clear();
        routeARPositions.Clear();
        selectedRouteIndices.Clear();

        yield return StartCoroutine(RequestNavigationRouteWithMode());
    }

    #endregion

    #region Route Management
    // This is the final, definitive version.
    // It combines your robust logic with the SDK-compatible API call.
    private IEnumerator RequestRealRouteWithProfile()
    {
        LogAR("🌐 Querying real Mapbox Directions API...");
        bool queryCompleted = false;
        DirectionsResponse apiResponse = null;
        Vector2d[] waypoints = new Vector2d[] { currentPosition, destination };

        // --- START: SDK v2.1.1 COMPATIBLE SETUP ---
        // We can only use the properties that exist in your older SDK.
        DirectionResource dr = new DirectionResource(waypoints, routeProfile);
        dr.Steps = true;
        dr.Overview = Mapbox.Directions.Overview.Full; // Maximum detail
                                                       // --- END: SDK v2.1.1 COMPATIBLE SETUP ---

        // Your excellent pre-flight distance check
        double distance = CalculateDistanceBetweenPoints(currentPosition, destination);
        LogAR($"Route distance: {distance:F1}m (straight line)");
        if (distance < 50)
        {
            LogAR("⚠️ Route is very short - API may return minimal geometry");
        }

        LogAR($"API Request details: Profile={routeProfile}, Overview={dr.Overview}");

        _directions.Query(dr, (DirectionsResponse res) =>
        {
            apiResponse = res;
            queryCompleted = true;
            // Your detailed response logging is great and remains here
            if (res != null && res.Routes != null && res.Routes.Count > 0)
            {
                var route = res.Routes[0];
                LogAR($"API SUCCESS: Received route with {route.Geometry?.Count ?? 0} geometry points");
            }
            else { LogErrorAR("API returned null or empty routes"); }
        });

        // Wait for the query to complete
        float timeout = 15f;
        float elapsed = 0f;
        while (!queryCompleted && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (queryCompleted && apiResponse != null && apiResponse.Routes != null && apiResponse.Routes.Count > 0)
        {
            var route = apiResponse.Routes[0];
            rawRoutePoints = route.Geometry;

            if (rawRoutePoints != null && rawRoutePoints.Count > 0)
            {
                routeReceived = true;
                LogAR($"✅ Real route fetched: {rawRoutePoints.Count} geometry points");

                // Your excellent post-flight check to enhance sparse routes
                if (rawRoutePoints.Count <= 5 && route.Legs != null)
                {
                    LogAR($"Only {rawRoutePoints.Count} geometry points - attempting to extract from steps...");
                    List<Vector2d> enhancedPoints = ExtractPointsFromSteps(route);
                    if (enhancedPoints.Count > rawRoutePoints.Count)
                    {
                        LogAR($"SUCCESS: Enhanced route to {enhancedPoints.Count} points from steps!");
                        rawRoutePoints = enhancedPoints;
                    }
                }
            }
            else
            {
                LogErrorAR("Route geometry is null or empty");
                routeReceived = false;
            }
        }
        else
        {
            LogErrorAR($"Real route query failed or timed out after {elapsed:F1}s");
            routeReceived = false;
        }

        // Fallback to test route ONLY if the real route failed
        if (!routeReceived)
        {
            LogAR("Falling back to test route.");
            rawRoutePoints = GenerateTestRoute(currentPosition, destination, 25);
            routeReceived = (rawRoutePoints != null && rawRoutePoints.Count > 0);
        }

        LogAR($"Final result: {rawRoutePoints?.Count ?? 0} route points, routeReceived: {routeReceived}");
    }


    // Add this NEW helper method to your script
    private List<Vector2d> ExtractPointsFromSteps(Mapbox.Directions.Route route)
    {
        List<Vector2d> points = new List<Vector2d>();
        if (route.Legs != null)
        {
            foreach (var leg in route.Legs)
            {
                if (leg.Steps != null)
                {
                    foreach (var step in leg.Steps)
                    {
                        if (step.Maneuver != null)
                        {
                            points.Add(step.Maneuver.Location);
                        }
                    }
                }
            }
        }
        return points;
    }


    // Add this NEW helper method to your script
    private double CalculateDistanceBetweenPoints(Vector2d point1, Vector2d point2)
    {
        double lat1Rad = point1.x * Math.PI / 180.0;
        double lat2Rad = point2.x * Math.PI / 180.0;
        double deltaLatRad = (point2.x - point1.x) * Math.PI / 180.0;
        double deltaLonRad = (point2.y - point1.y) * Math.PI / 180.0;

        double a = Math.Sin(deltaLatRad / 2) * Math.Sin(deltaLatRad / 2) +
                     Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                     Math.Sin(deltaLonRad / 2) * Math.Sin(deltaLonRad / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return 6371000 * c; // Earth radius in meters
    }

    // FIXED: ProcessRouteData method
    private IEnumerator ProcessRouteData()
    {
        LogAR("=== ENHANCED ROUTE PROCESSING START ===");

        if (rawRoutePoints == null || rawRoutePoints.Count == 0)
        {
            LogErrorAR("No route points to process");
            yield break;
        }

        routeOrigin = rawRoutePoints[0];
        routeARPositions.Clear();
        selectedRouteIndices.Clear();
        ClearExistingArrows();

        // Convert GPS to AR coordinates
        for (int i = 0; i < rawRoutePoints.Count; i++)
        {
            Vector2d gpsPoint = rawRoutePoints[i];
            Vector3 arPosition = ConvertGPSToAR(gpsPoint);
            routeARPositions.Add(arPosition);

            if (i % 3 == 2) yield return null; // Prevent frame drops
        }

        // Center route for indoor testing
        if (useTestRoute && routeARPositions.Count > 0)
        {
            CenterRouteForIndoorTesting();
        }

        // Calculate distances and optimal spacing
        CalculateOptimalArrowSpacing();

        // Use improved selection algorithm
        selectedRouteIndices = SelectRoutePointsImproved(routeARPositions, arrowSpacing);

        LogAR($"Route processed: {selectedRouteIndices.Count} arrow positions selected");
        UpdateStatus($"Route ready: {selectedRouteIndices.Count} arrows planned");

        // Initialize dynamic arrow system
        arrowsSpawned = true;
        lastUserPositionForUpdate = userARPosition;

        // Spawn initial arrows
        SpawnInitialGuidanceArrows();

        LogAR("=== ROUTE PROCESSING COMPLETED ===");
    }

    private void CenterRouteForIndoorTesting()
    {
        if (routeARPositions.Count == 0) return;

        // Calculate centroid
        Vector3 centroid = Vector3.zero;
        foreach (var pos in routeARPositions)
        {
            centroid += pos;
        }
        centroid /= routeARPositions.Count;
        centroid.y = 0f;

        // Position route in front of camera
        Vector3 cameraForwardOffset = arCamera.transform.forward * 3f;
        cameraForwardOffset.y = 0f;
        Quaternion cameraYawRot = Quaternion.Euler(0f, arCamera.transform.eulerAngles.y, 0f);

        // Reposition and rotate every point
        for (int i = 0; i < routeARPositions.Count; i++)
        {
            routeARPositions[i] -= centroid;
            routeARPositions[i] = cameraYawRot * routeARPositions[i];
            routeARPositions[i] += cameraForwardOffset;
        }

        LogAR("Route centered and rotated for indoor testing");
    }
    private IEnumerator ProcessInitialSegment()
    {
        LogAR("=== PROCESSING INITIAL ROUTE SEGMENT ===");

        if (rawRoutePoints == null || rawRoutePoints.Count == 0)
        {
            LogErrorAR("No raw route points to process");
            yield break;
        }

        // For outdoor real routes, don't limit the points unless absolutely necessary
        if (!useTestRoute && rawRoutePoints.Count > maxPointsPerSegment)
        {
            LogAR($"Long route detected: {rawRoutePoints.Count} points, processing first {maxPointsPerSegment}");
            // Keep the full route but process in segments for performance
            // For now, process all points but we could optimize this later
        }

        // Process the full route data
        yield return StartCoroutine(ProcessRouteData());

        // Calculate distances for dynamic spawning
        CalculateCumulativeDistances();

        LogAR($"Initial segment processed: {routeARPositions.Count} AR positions, {totalRouteDistance:F1}m total");
    }

    // FIXED: GPS to AR conversion
    private Vector3 ConvertGPSToAR(Vector2d gpsPoint)
    {
        double latDiff = gpsPoint.x - routeOrigin.x;
        double lonDiff = gpsPoint.y - routeOrigin.y;

        double metersPerDegreeLat = 111320.0;
        double metersPerDegreeLon = 111320.0 * System.Math.Cos(routeOrigin.x * System.Math.PI / 180.0);

        double xMeters = lonDiff * metersPerDegreeLon;
        double zMeters = latDiff * metersPerDegreeLat;

        // FIXED: Apply indoor scaling correctly
        if (useTestRoute)
        {
            // For indoor testing, we want the route to appear at a reasonable size
            // The indoor scale factor makes coordinates visible in AR
            xMeters *= indoorScaleFactor;
            zMeters *= indoorScaleFactor;

            LogAR($"   Indoor scaled: X={xMeters:F6}m, Z={zMeters:F6}m (scale factor: {indoorScaleFactor})");
        }

        float x = (float)xMeters;
        float z = (float)zMeters;
        float y = 0f;

        Vector3 result = new Vector3(x, y, z);
        result = northAlignment * result;
        LogAR($"   AR Pos after alignment: ({result.x:F3}, {result.z:F3}), North heading: {Input.compass.trueHeading:F1}");
        return result;
    }
    // 4. ADDITIONAL: Add this method to test with better settings
[ContextMenu("Test Indoor Route with Optimal Settings")]
public void TestIndoorRouteOptimal()
{
    LogAR("🧪 TESTING WITH OPTIMAL INDOOR SETTINGS...");
    
    // Temporarily adjust settings for better indoor experience
    float originalSpacing = arrowSpacing;
    int originalMaxArrows = maxArrows;
    
    arrowSpacing = 2f; // Closer spacing for indoor
    maxArrows = 6;     // Fewer arrows for clarity
    
    LogAR($"🔧 Adjusted settings: spacing={arrowSpacing}m, maxArrows={maxArrows}");
    
    // Clear and regenerate
    ClearExistingArrows();
    rawRoutePoints.Clear();
    routeARPositions.Clear();
    selectedRouteIndices.Clear();
    routeRequested = false;
    routeReceived = false;
    arrowsSpawned = false;
    
    // Start fresh route generation
    StartCoroutine(RequestNavigationRouteWithMode());
    
    LogAR("✅ Testing with optimal indoor settings...");
}

    // FIXED: SelectRoutePoints method
    // ADJUSTED STEP 1: Modified SelectRoutePoints for your settings
    private List<int> SelectRoutePoints(List<Vector3> arPositions, float minSpacing)
    {
        List<int> selectedIndices = new List<int>();

        if (arPositions.Count == 0) return selectedIndices;
        if (arPositions.Count == 1)
        {
            selectedIndices.Add(0);
            return selectedIndices;
        }

        LogAR("🎯 SELECTION ALGORITHM START:");
        LogAR($"   Input: {arPositions.Count} AR positions");
        LogAR($"   Min spacing: {minSpacing:F2}m");
        LogAR($"   Max arrows: {maxArrows}");

        // Compute cumulative distances
        List<float> cumDist = new List<float> { 0f };
        for (int i = 1; i < arPositions.Count; i++)
        {
            float d = Vector3.Distance(arPositions[i], arPositions[i - 1]);
            cumDist.Add(cumDist[i - 1] + d);
        }
        float totalDist = cumDist[cumDist.Count - 1];
        LogAR($"   Total AR distance: {totalDist:F2}m");

        // Determine number of arrows: respect min spacing and maxArrows
        int maxPossibleArrows = Mathf.FloorToInt(totalDist / minSpacing) + 1;
        int numArrows = Mathf.Min(maxArrows, maxPossibleArrows);
        numArrows = Mathf.Max(numArrows, 2); // Always at least start + end
        LogAR($"   Selecting {numArrows} arrows (max possible: {maxPossibleArrows})");

        float idealSpacing = totalDist / (numArrows - 1f);
        LogAR($"   Ideal spacing: {idealSpacing:F2}m");

        // Always include start
        selectedIndices.Add(0);
        LogAR($"✅ Selected START: Index 0, Position: ({arPositions[0].x:F2}, {arPositions[0].y:F2}, {arPositions[0].z:F2}), CumDist: 0.00");

        // Select intermediate points evenly
        for (int k = 1; k < numArrows - 1; k++)
        {
            float targetDist = k * idealSpacing;
            float bestDiff = float.MaxValue;
            int bestIndex = -1;

            // Find closest index after last selected
            int startSearch = selectedIndices[selectedIndices.Count - 1] + 1;
            for (int j = startSearch; j < arPositions.Count - 1; j++)
            {
                float diff = Mathf.Abs(cumDist[j] - targetDist);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIndex = j;
                }
                else
                {
                    break; // Since cumDist increasing
                }
            }

            if (bestIndex != -1)
            {
                selectedIndices.Add(bestIndex);
                Vector3 pos = arPositions[bestIndex];
                LogAR($"✅ Selected WAYPOINT: Index {bestIndex}, Position: ({pos.x:F2}, {pos.y:F2}, {pos.z:F2}), CumDist: {cumDist[bestIndex]:F2}, Target: {targetDist:F2}");
            }
        }

        // Always include end
        int endIndex = arPositions.Count - 1;
        if (!selectedIndices.Contains(endIndex))
        {
            selectedIndices.Add(endIndex);
            Vector3 endPos = arPositions[endIndex];
            float finalDist = cumDist[endIndex] - cumDist[selectedIndices[selectedIndices.Count - 2]];
            LogAR($"✅ Selected END: Index {endIndex}, Position: ({endPos.x:F2}, {endPos.y:F2}, {endPos.z:F2}), Distance from last: {finalDist:F2}m");
        }

        LogAR($"🎉 SELECTION COMPLETE: {selectedIndices.Count} points selected");
        return selectedIndices;
    }
    private void UpdateDynamicArrowSpawning()
    {
        if (routeARPositions == null || routeARPositions.Count < 2 || primaryPlane == null)
        {
            return;
        }

        // Calculate user's progress along the route
        UserProgressData progress = CalculateUserProgress();

        LogAR($"🔄 Dynamic update: User at {progress.distanceAlongRoute:F1}m along route");

        // Define visibility window based on user's current position
        float windowStart = Mathf.Max(0, progress.distanceAlongRoute - despawnBehindDistance);
        float windowEnd = Mathf.Min(totalRouteDistance, progress.distanceAlongRoute + spawnAheadDistance);

        LogAR($"Visibility window: {windowStart:F1}m to {windowEnd:F1}m");

        // Determine which arrows should exist in this window
        HashSet<int> requiredIndices = new HashSet<int>();
        float lastSpawnDistance = windowStart - arrowSpacing; // Start before window

        for (int i = 0; i < routeARPositions.Count; i++)
        {
            float routeDistance = cumulativeDistances[i];

            // Check if this point is in our visibility window
            if (routeDistance >= windowStart && routeDistance <= windowEnd)
            {
                // Check spacing requirement
                if (routeDistance - lastSpawnDistance >= arrowSpacing ||
                    i == 0 || i == routeARPositions.Count - 1) // Always include start/end if in window
                {
                    requiredIndices.Add(i);
                    lastSpawnDistance = routeDistance;
                    LogAR($"Required arrow at index {i}, distance {routeDistance:F1}m");
                }
            }
        }

        // Ensure minimum arrow count for guidance
        if (requiredIndices.Count < minimumVisibleArrows)
        {
            LogAR($"Only {requiredIndices.Count} arrows planned, ensuring minimum {minimumVisibleArrows}");

            // Expand window or reduce spacing to get more arrows
            float expandedEnd = Mathf.Min(totalRouteDistance, progress.distanceAlongRoute + spawnAheadDistance * 1.5f);
            float reducedSpacing = arrowSpacing * 0.6f;
            lastSpawnDistance = windowStart - reducedSpacing;

            for (int i = 0; i < routeARPositions.Count && requiredIndices.Count < minimumVisibleArrows; i++)
            {
                float routeDistance = cumulativeDistances[i];

                if (routeDistance >= windowStart && routeDistance <= expandedEnd)
                {
                    if (routeDistance - lastSpawnDistance >= reducedSpacing)
                    {
                        if (!requiredIndices.Contains(i))
                        {
                            requiredIndices.Add(i);
                            lastSpawnDistance = routeDistance;
                            LogAR($"Added extra arrow at index {i} for minimum count");
                        }
                    }
                }
            }
        }

        // Remove arrows that shouldn't exist anymore
        List<GameObject> arrowsToRemove = new List<GameObject>();
        foreach (var arrow in managedArrowObjects.ToList()) // ToList() to avoid modification during iteration
        {
            if (arrow != null)
            {
                ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
                if (metadata != null)
                {
                    if (!requiredIndices.Contains(metadata.RouteIndex))
                    {
                        arrowsToRemove.Add(arrow);
                        LogAR($"Marking arrow {metadata.RouteIndex} for removal (distance: {metadata.DistanceFromStart:F1}m)");
                    }
                }
            }
            else
            {
                // Remove null references
                arrowsToRemove.Add(arrow);
            }
        }

        // Remove obsolete arrows
        foreach (var arrow in arrowsToRemove)
        {
            RemoveArrow(arrow);
        }

        // Spawn new arrows that should exist
        List<int> newIndices = new List<int>();
        foreach (int index in requiredIndices)
        {
            bool exists = false;
            foreach (var arrow in managedArrowObjects)
            {
                if (arrow != null)
                {
                    ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
                    if (metadata != null && metadata.RouteIndex == index)
                    {
                        exists = true;
                        break;
                    }
                }
            }

            if (!exists)
            {
                newIndices.Add(index);
            }
        }

        // Spawn new arrows
        if (newIndices.Count > 0)
        {
            LogAR($"Spawning {newIndices.Count} new arrows");
            StartCoroutine(SpawnArrowsFromIndices(newIndices));
        }

        LogAR($"Dynamic update complete: {managedArrowObjects.Count} total arrows active");
    }
    private void RemoveArrow(GameObject arrow)
    {
        if (arrow == null) return;

        // Get metadata for logging
        ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
        int routeIndex = metadata != null ? metadata.RouteIndex : -1;

        LogAR($"Removing arrow {routeIndex}");

        // Remove from all tracking lists
        managedArrowObjects.Remove(arrow);
        activeArrows.Remove(arrow);
        spawnedArrows.Remove(arrow);

        // Find and remove the corresponding anchor
        Transform anchorTransform = arrow.transform.parent;
        if (anchorTransform != null)
        {
            ARAnchor anchor = anchorTransform.GetComponent<ARAnchor>();
            if (anchor != null)
            {
                arrowAnchors.Remove(anchor);
                LogAR($"Destroying anchor for arrow {routeIndex}");
            }

            // Destroy the entire anchor object (which includes the arrow)
            Destroy(anchorTransform.gameObject);
        }
        else
        {
            // If somehow the arrow has no parent anchor, destroy it directly
            LogAR($"Warning: Arrow {routeIndex} has no parent anchor, destroying directly");
            Destroy(arrow);
        }
    }
    private List<int> SelectRoutePointsImproved(List<Vector3> arPositions, float minSpacing)
    {
        List<int> selectedIndices = new List<int>();

        if (arPositions.Count == 0) return selectedIndices;

        // Calculate cumulative distances
        List<float> cumDistances = new List<float> { 0f };
        for (int i = 1; i < arPositions.Count; i++)
        {
            float distance = Vector3.Distance(arPositions[i], arPositions[i - 1]);
            cumDistances.Add(cumDistances[i - 1] + distance);
        }

        float totalDistance = cumDistances[cumDistances.Count - 1];

        // Always include start point
        selectedIndices.Add(0);

        // Select points based on distance intervals
        float currentTargetDistance = minSpacing;
        int lastSelectedIndex = 0;

        for (int i = 1; i < arPositions.Count - 1; i++)
        {
            if (cumDistances[i] >= currentTargetDistance &&
                i > lastSelectedIndex + 1) // Ensure we don't select consecutive points
            {
                selectedIndices.Add(i);
                lastSelectedIndex = i;
                currentTargetDistance = cumDistances[i] + minSpacing;

                if (selectedIndices.Count >= maxArrows - 1) break; // Save space for end point
            }
        }

        // Always include end point
        if (selectedIndices[selectedIndices.Count - 1] != arPositions.Count - 1)
        {
            selectedIndices.Add(arPositions.Count - 1);
        }

        LogAR($"Route selection: {selectedIndices.Count} points selected from {arPositions.Count} total");
        return selectedIndices;
    }

    private IEnumerator RequestNavigationRouteWithMode()
    {
        LogAR("=== ROUTE REQUEST WITH NAVIGATION MODE ===");

        // Clear existing state
        ClearExistingArrows();
        rawRoutePoints.Clear();
        routeARPositions.Clear();
        selectedRouteIndices.Clear();
        routeReceived = false;
        arrowsSpawned = false;

        routeRequested = true;
        UpdateStatus($"Getting {currentMode} route...");

        bool success = false;

        if (useTestRoute)
        {
            // Generate test route based on mode
            int pointCount = GetPointCountForMode(currentMode);
            rawRoutePoints = GenerateTestRouteForMode(currentPosition, destination, pointCount, currentMode);

            if (rawRoutePoints != null && rawRoutePoints.Count > 0)
            {
                routeReceived = true;
                success = true;
                LogAR($"Test route generated: {rawRoutePoints.Count} points for {currentMode} mode");
            }
        }
        else
        {
            // Real API call with correct routing profile
            yield return StartCoroutine(RequestRealRouteWithProfile());
            success = routeReceived;
        }

        if (success)
        {
            yield return StartCoroutine(ProcessInitialSegment());
        }

        LogAR("=== ROUTE REQUEST COMPLETED ===");
    }
    [System.Serializable]
    public class UserProgressData
    {
        public float distanceAlongRoute;
        public int nearestRouteIndex;
        public Vector3 projectedPosition;
        public float distanceFromRoute;
    }

    [System.Serializable]
    public class ArrowSpawnData
    {
        public int routeIndex;
        public Vector3 arPosition;
        public Vector3 worldPosition;
        public Vector2d gpsPosition;
        public float distanceFromStart;
        public Vector3 direction;
        public bool shouldExist;
    }

    private UserProgressData CalculateUserProgress()
    {
        UserProgressData progress = new UserProgressData();
        float closestDistance = float.MaxValue;

        Vector3 userPosFlat = new Vector3(userARPosition.x, 0, userARPosition.z);

        for (int i = 0; i < routeARPositions.Count - 1; i++)
        {
            Vector3 segmentStart = new Vector3(routeARPositions[i].x, 0, routeARPositions[i].z);
            Vector3 segmentEnd = new Vector3(routeARPositions[i + 1].x, 0, routeARPositions[i + 1].z);

            Vector3 closestPoint = FindNearestPointOnLine(segmentStart, segmentEnd, userPosFlat);
            float distance = Vector3.Distance(userPosFlat, closestPoint);

            if (distance < closestDistance)
            {
                closestDistance = distance;
                progress.nearestRouteIndex = i;
                progress.projectedPosition = closestPoint;
                progress.distanceFromRoute = distance;

                // Calculate distance along route
                progress.distanceAlongRoute = cumulativeDistances[i] +
                    Vector3.Distance(segmentStart, closestPoint);
            }
        }

        return progress;
    }

    private Vector3 CalculateStableWorldPosition(Vector3 arPosition)
    {
        // Convert AR position to stable world position
        Vector3 worldPos = primaryPlane.transform.TransformPoint(arPosition);

        // Project onto detected ground plane using raycast
        Vector2 screenPoint = arCamera.WorldToScreenPoint(worldPos);
        List<ARRaycastHit> hits = new List<ARRaycastHit>();

        if (raycastManager.Raycast(screenPoint, hits, TrackableType.Planes))
        {
            worldPos = hits[0].pose.position;
            worldPos.y += arrowHeightOffset;
        }
        else
        {
            // Fallback to plane height
            worldPos.y = primaryPlane.transform.position.y + arrowHeightOffset;
        }

        return worldPos;
    }

    private Vector3 CalculateArrowDirection(int routeIndex)
    {
        Vector3 direction = arCamera.transform.forward; // Fallback

        if (routeIndex < routeARPositions.Count - 1)
        {
            Vector3 current = routeARPositions[routeIndex];
            Vector3 next = routeARPositions[routeIndex + 1];

            direction = (next - current).normalized;
            direction.y = 0f; // Keep horizontal

            if (direction.magnitude < 0.1f)
            {
                // Look ahead further if points are too close
                for (int i = routeIndex + 2; i < Mathf.Min(routeIndex + 5, routeARPositions.Count); i++)
                {
                    Vector3 futurePoint = routeARPositions[i];
                    direction = (futurePoint - current).normalized;
                    direction.y = 0f;

                    if (direction.magnitude > 0.1f) break;
                }
            }
        }

        return direction;
    }
    private List<Vector2d> GenerateTestRouteForMode(Vector2d start, Vector2d end, int totalPoints, NavigationMode mode)
    {
        List<Vector2d> route = new List<Vector2d>();

        // Adjust route characteristics based on mode
        double routeDistance = GetDistanceForMode(mode);
        double complexity = GetComplexityForMode(mode);

        double metersPerDegreeLat = 111320.0;
        double metersPerDegreeLon = 111320.0 * System.Math.Cos(start.x * System.Math.PI / 180.0);

        double latRange = routeDistance / metersPerDegreeLat;
        double lonRange = routeDistance / metersPerDegreeLon;

        LogAR($"Generating {mode} route: {routeDistance}m distance, {complexity} complexity");

        route.Add(start);

        for (int i = 1; i < totalPoints - 1; i++)
        {
            float progress = (float)i / (totalPoints - 1);
            Vector2d point;

            if (mode == NavigationMode.Walking)
            {
                // Walking routes can have more turns and shortcuts
                if (progress <= 0.7f)
                {
                    float segmentProgress = progress / 0.7f;
                    point = new Vector2d(
                        start.x + latRange * 0.6 * segmentProgress,
                        start.y + lonRange * 0.3 * segmentProgress
                    );
                }
                else
                {
                    float segmentProgress = (progress - 0.7f) / 0.3f;
                    Vector2d segmentStart = new Vector2d(start.x + latRange * 0.6, start.y + lonRange * 0.3);
                    point = new Vector2d(
                        segmentStart.x + latRange * 0.4 * segmentProgress,
                        segmentStart.y + lonRange * 0.7 * segmentProgress
                    );
                }
            }
            else if (mode == NavigationMode.Cycling)
            {
                // Cycling routes prefer bike lanes and smoother turns
                point = new Vector2d(
                    start.x + latRange * progress,
                    start.y + lonRange * progress * 0.8
                );
            }
            else // Driving
            {
                // Driving routes follow roads more strictly
                point = new Vector2d(
                    start.x + latRange * progress * 0.9,
                    start.y + lonRange * progress
                );
            }

            route.Add(point);
        }

        // Adjust end point based on mode
        Vector2d adjustedEnd = new Vector2d(
            start.x + latRange,
            start.y + lonRange
        );
        route.Add(adjustedEnd);

        return route;
    }
    private int GetPointCountForMode(NavigationMode mode)
    {
        switch (mode)
        {
            case NavigationMode.Walking: return 25; // More detailed for walking
            case NavigationMode.Cycling: return 20; // Medium detail
            case NavigationMode.Driving: return 15; // Less detail for driving
            default: return 20;
        }
    }
    private double GetDistanceForMode(NavigationMode mode)
    {
        switch (mode)
        {
            case NavigationMode.Walking: return 150.0; // Shorter walking routes
            case NavigationMode.Cycling: return 300.0; // Medium cycling routes  
            case NavigationMode.Driving: return 500.0; // Longer driving routes
            default: return 200.0;
        }
    }
    private double GetComplexityForMode(NavigationMode mode)
    {
        switch (mode)
        {
            case NavigationMode.Walking: return 1.0; // Can take shortcuts
            case NavigationMode.Cycling: return 0.8; // Some restrictions
            case NavigationMode.Driving: return 0.6; // Must follow roads
            default: return 1.0;
        }
    }
    private void CalculateOptimalArrowSpacing()
    {
        if (totalRouteDistance <= 0) return;

        // Adjust spacing based on route length and navigation mode
        float baseSpacing = arrowSpacing;

        if (totalRouteDistance < 100f)
        {
            // Short routes need closer arrows
            arrowSpacing = Mathf.Max(baseSpacing * 0.5f, 2f);
        }
        else if (totalRouteDistance > 1000f)
        {
            // Long routes can have wider spacing
            arrowSpacing = baseSpacing * 1.5f;
        }

        // Mode-specific adjustments
        switch (currentMode)
        {
            case NavigationMode.Walking:
                arrowSpacing *= 0.8f; // Closer for walking
                break;
            case NavigationMode.Cycling:
                arrowSpacing *= 1.0f; // Normal spacing
                break;
            case NavigationMode.Driving:
                arrowSpacing *= 1.3f; // Wider for driving
                break;
        }

        LogAR($"Optimal arrow spacing calculated: {arrowSpacing:F1}m for {currentMode} mode");
    }
    public static Vector3 FindNearestPointOnLine(Vector3 origin, Vector3 end, Vector3 point)
    {
        Vector3 heading = end - origin;
        float magnitudeMax = heading.magnitude;
        heading.Normalize();

        Vector3 lhs = point - origin;
        float dotP = Vector3.Dot(lhs, heading);
        dotP = Mathf.Clamp(dotP, 0f, magnitudeMax);
        return origin + heading * dotP;
    }

    private void SpawnInitialGuidanceArrows()
    {
        LogAR("=== SPAWNING INITIAL GUIDANCE ARROWS ===");

        // Calculate cumulative distances for the entire route
        CalculateCumulativeDistances();

        if (routeARPositions.Count == 0)
        {
            LogErrorAR("No route positions available for arrow spawning");
            return;
        }

        // Clear any existing arrows first
        ClearExistingArrows();

        // Calculate how many arrows we should spawn initially
        int targetArrowCount = Mathf.Max(minimumVisibleArrows, 8); // Ensure good visibility
        targetArrowCount = Mathf.Min(targetArrowCount, maxArrows); // Don't exceed max

        List<int> initialIndices = new List<int>();

        // Always include start point
        initialIndices.Add(0);

        // Calculate optimal spacing for initial arrows
        float effectiveSpacing = arrowSpacing;

        // If we have a long route, we might need to adjust spacing to get enough initial arrows
        if (totalRouteDistance > initialSpawnDistance)
        {
            int maxPossibleArrows = Mathf.FloorToInt(initialSpawnDistance / arrowSpacing);
            if (maxPossibleArrows < targetArrowCount)
            {
                effectiveSpacing = initialSpawnDistance / targetArrowCount;
                LogAR($"Adjusting initial spacing to {effectiveSpacing:F1}m to fit {targetArrowCount} arrows");
            }
        }

        float lastSpawnedDistance = 0f;

        // Add arrows within initial spawn distance
        for (int i = 1; i < routeARPositions.Count; i++)
        {
            float currentDistance = cumulativeDistances[i];

            // Stop if we've exceeded initial spawn distance and have minimum arrows
            if (currentDistance > initialSpawnDistance && initialIndices.Count >= minimumVisibleArrows)
                break;

            // Check spacing requirement
            float distanceFromLast = currentDistance - lastSpawnedDistance;
            if (distanceFromLast >= effectiveSpacing)
            {
                initialIndices.Add(i);
                lastSpawnedDistance = currentDistance;

                LogAR($"Added initial arrow at index {i}, distance {currentDistance:F1}m");

                // Stop if we have enough arrows
                if (initialIndices.Count >= targetArrowCount) break;
            }
        }

        // Ensure we have the destination arrow if route is short
        int lastIndex = routeARPositions.Count - 1;
        if (!initialIndices.Contains(lastIndex) && cumulativeDistances[lastIndex] <= initialSpawnDistance * 1.2f)
        {
            initialIndices.Add(lastIndex);
        }

        LogAR($"Initial spawn plan: {initialIndices.Count} arrows planned");

        // Spawn the arrows synchronously for immediate visibility
        StartCoroutine(SpawnArrowsFromIndices(initialIndices));
    }
    private IEnumerator SpawnArrowsFromIndices(List<int> indices)
    {
        LogAR($"=== SPAWNING ARROWS FROM {indices.Count} INDICES ===");

        if (primaryPlane == null)
        {
            LogErrorAR("Primary plane is null - cannot spawn arrows");
            yield break;
        }

        if (arrowPrefab == null)
        {
            LogErrorAR("Arrow prefab is null - check inspector assignment");
            yield break;
        }

        int successfulSpawns = 0;

        for (int i = 0; i < indices.Count; i++)
        {
            int routeIndex = indices[i];

            if (routeIndex >= routeARPositions.Count)
            {
                LogErrorAR($"Invalid route index: {routeIndex}");
                continue;
            }

            Vector3 arPosition = routeARPositions[routeIndex];

            // Project position onto the ground plane with raycast
            Vector3 worldPosition = ProjectPositionToGround(arPosition);

            // Move try-catch outside the yield-containing code
            GameObject anchorObject = null;
            ARAnchor anchor = null;
            GameObject arrow = null;
            bool spawnSuccess = false;

            // Create anchor object
            anchorObject = new GameObject($"NavArrowAnchor_{routeIndex}");
            anchorObject.transform.position = worldPosition;
            anchorObject.transform.rotation = Quaternion.identity;

            anchor = anchorObject.AddComponent<ARAnchor>();
            if (anchor == null)
            {
                LogErrorAR($"Failed to create anchor for arrow {routeIndex}");
                if (anchorObject != null) Destroy(anchorObject);
                continue;
            }

            // Create arrow as child of anchor
            arrow = Instantiate(arrowPrefab, anchor.transform);
            if (arrow != null)
            {
                arrow.name = $"NavArrow_{routeIndex}";
                arrow.transform.localPosition = Vector3.zero;
                arrow.transform.localScale = Vector3.one * arrowScale;

                // Calculate direction for arrow rotation
                // --- FINAL ROTATION LOGIC (Fixes model orientation AND keeps it flat) ---
                Vector3 direction = CalculateArrowDirection(routeIndex);
                if (direction.sqrMagnitude > 0.01f)
                {
                    // 1. Get the rotation that points the arrow along the path
                    Quaternion pathRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

                    // 2. IMPORTANT: Create a correction for models that point UP (Y-axis) instead of FORWARD (Z-axis)
                    Quaternion modelCorrection = Quaternion.FromToRotation(Vector3.up, Vector3.forward);

                    // 3. Get the user's manual offset (should be 0)
                    Quaternion userOffset = Quaternion.Euler(0f, arrowYawOffsetDegrees, 0f);

                    // 4. Combine all rotations: First apply the main path rotation, THEN the model correction
                    arrow.transform.rotation = primaryPlane.transform.rotation * pathRotation * modelCorrection * userOffset;
                }

                // Set appropriate color
                Color arrowColor = Color.yellow;
                if (routeIndex == 0) arrowColor = Color.green;
                else if (routeIndex == routeARPositions.Count - 1) arrowColor = Color.blue;

                // Apply color to all renderers
                Renderer[] renderers = arrow.GetComponentsInChildren<Renderer>();
                foreach (var renderer in renderers)
                {
                    renderer.enabled = true;
                    renderer.material.color = arrowColor;
                }

                // Add metadata component
                ArrowMetadata metadata = arrow.AddComponent<ArrowMetadata>();
                metadata.RouteIndex = routeIndex;
                metadata.DistanceFromStart = cumulativeDistances[routeIndex];
                metadata.StableWorldPosition = worldPosition;
                metadata.LastUpdateTime = Time.time;

                // Track the arrow
                managedArrowObjects.Add(arrow);
                arrowAnchors.Add(anchor);
                activeArrows.Add(arrow);
                spawnedArrows.Add(arrow);

                successfulSpawns++;
                spawnSuccess = true;
                LogAR($"✅ Arrow {routeIndex} spawned at distance {metadata.DistanceFromStart:F1}m");
            }

            if (!spawnSuccess)
            {
                LogErrorAR($"Failed to spawn arrow {routeIndex}");
                if (arrow != null) Destroy(arrow);
                if (anchorObject != null) Destroy(anchorObject);
            }

            // Small delay to prevent frame drops - moved outside try-catch
            yield return new WaitForSeconds(0.05f);
        }

        arrowsSpawned = true;
        LogAR($"✅ Initial arrow spawning complete: {successfulSpawns}/{indices.Count} arrows spawned");
        UpdateStatus($"Navigation ready: {successfulSpawns} arrows visible");
    }
    private Vector3 ProjectPositionToGround(Vector3 arPosition)
    {
        // Transform AR position to world space
        Vector3 worldPos = primaryPlane.transform.TransformPoint(arPosition);

        // Create a ray from high above the target position downward
        Vector3 rayStart = worldPos + Vector3.up * 10f;
        Vector3 rayDirection = Vector3.down;

        // Try raycast to ground first
        RaycastHit hit;
        if (Physics.Raycast(rayStart, rayDirection, out hit, 20f))
        {
            Vector3 groundPos = hit.point;
            groundPos.y += arrowHeightOffset;
            LogAR($"Ground raycast hit at: {groundPos} (height: {groundPos.y:F2})");
            return groundPos;
        }

        // Fallback: Try AR raycast from camera to the approximate screen position
        Vector3 screenPos = arCamera.WorldToScreenPoint(worldPos);
        if (screenPos.x >= 0 && screenPos.x <= Screen.width &&
            screenPos.y >= 0 && screenPos.y <= Screen.height && screenPos.z > 0)
        {
            List<ARRaycastHit> arHits = new List<ARRaycastHit>();
            if (raycastManager.Raycast(new Vector2(screenPos.x, screenPos.y), arHits, TrackableType.Planes))
            {
                Vector3 arGroundPos = arHits[0].pose.position;
                arGroundPos.y += arrowHeightOffset;
                LogAR($"AR raycast hit at: {arGroundPos} (height: {arGroundPos.y:F2})");
                return arGroundPos;
            }
        }

        // Final fallback: Use primary plane height
        Vector3 fallbackPos = worldPos;
        fallbackPos.y = primaryPlane.transform.position.y + arrowHeightOffset;
        LogAR($"Using fallback plane height: {fallbackPos} (plane Y: {primaryPlane.transform.position.y:F2})");

        return fallbackPos;
    }
    private void CalculateCumulativeDistances()
    {
        cumulativeDistances.Clear();
        cumulativeDistances.Add(0f);
        totalRouteDistance = 0f;

        for (int i = 1; i < routeARPositions.Count; i++)
        {
            float segmentDistance = Vector3.Distance(routeARPositions[i], routeARPositions[i - 1]);
            totalRouteDistance += segmentDistance;
            cumulativeDistances.Add(totalRouteDistance);
        }

        LogAR($"Route analysis: Total distance {totalRouteDistance:F1}m across {routeARPositions.Count} points");
    }

    private List<Vector2d> GenerateTestRoute(Vector2d start, Vector2d end, int totalPoints)
    {
        LogAR($"🧪 GENERATING INDOOR-OPTIMIZED TEST ROUTE:");
        LogAR($"   Start: {start.x:F8}, {start.y:F8}");
        LogAR($"   End: {end.x:F8}, {end.y:F8}");
        LogAR($"   Requested points: {totalPoints}");
        LogAR($"   Settings: Indoor scale={indoorScaleFactor}, Arrow spacing={arrowSpacing}m");

        List<Vector2d> route = new List<Vector2d>();

        // FIXED: For indoor testing, create a much smaller route
        // Instead of 200m real-world route, create a 20m route that scales to 200m
        double realWorldDistance = 50.0; // meters
        double metersPerDegreeLat = 111320.0;
        double metersPerDegreeLon = 111320.0 * System.Math.Cos(start.x * System.Math.PI / 180.0);

        // Convert desired distance to GPS degrees
        double latRange = realWorldDistance / metersPerDegreeLat;
        double lonRange = realWorldDistance / metersPerDegreeLon;

        LogAR($"   Creating {realWorldDistance}m route for indoor testing");
        LogAR($"   GPS ranges: Lat={latRange:F8}, Lon={lonRange:F8}");

        // Always start with start point
        route.Add(start);

        // Generate intermediate points along a realistic walking path
        for (int i = 1; i < totalPoints - 1; i++)
        {
            float progress = (float)i / (totalPoints - 1);
            Vector2d point;

            // Create a simple L-shaped route for indoor testing
            if (progress <= 0.6f)
            {
                // First segment: move north
                float segmentProgress = progress / 0.6f;
                point = new Vector2d(
                    start.x + latRange * 0.8 * segmentProgress,  // 80% north movement
                    start.y + lonRange * 0.1 * segmentProgress   // 10% east movement
                );
            }
            else
            {
                // Second segment: turn east
                float segmentProgress = (progress - 0.6f) / 0.4f;
                Vector2d segmentStart = new Vector2d(
                    start.x + latRange * 0.8,
                    start.y + lonRange * 0.1
                );
                point = new Vector2d(
                    segmentStart.x + latRange * 0.2 * segmentProgress,  // Complete north
                    segmentStart.y + lonRange * 0.9 * segmentProgress   // Major east movement
                );
            }

            route.Add(point);

            if (i % 5 == 0 || i < 5)
            {
                LogAR($"   Point[{i}]: {point.x:F8}, {point.y:F8} (progress: {progress:F2})");
            }
        }

        // Always end with destination (adjusted to be within small range)
        Vector2d adjustedEnd = new Vector2d(
            start.x + latRange,
            start.y + lonRange
        );
        route.Add(adjustedEnd);

        LogAR($"🎉 Indoor-optimized route generated: {route.Count} points");
        LogAR($"📐 Real-world span: {realWorldDistance}m, AR span after scaling: {realWorldDistance * indoorScaleFactor}m");

        return route;
    }
    [ContextMenu("Debug API Response Raw")]
    public void DebugAPIResponseRaw()
    {
        StartCoroutine(TestAPIResponseRaw());
    }

    private IEnumerator TestAPIResponseRaw()
    {
        LogAR("=== TESTING RAW API RESPONSE ===");

        Vector2d[] waypoints = new Vector2d[] { currentPosition, destination };
        DirectionResource dr = new DirectionResource(waypoints, RoutingProfile.Walking);
        dr.Steps = true;
        dr.Overview = Mapbox.Directions.Overview.Full;

        bool completed = false;
        DirectionsResponse response = null;

        _directions.Query(dr, (DirectionsResponse res) =>
        {
            response = res;
            completed = true;
        });

        yield return new WaitUntil(() => completed);

        if (response != null && response.Routes != null && response.Routes.Count > 0)
        {
            var route = response.Routes[0];
            LogAR($"Raw API Response:");
            LogAR($"  Routes count: {response.Routes.Count}");
            LogAR($"  Geometry points: {route.Geometry?.Count ?? 0}");
            LogAR($"  Distance: {route.Distance}m");
            LogAR($"  Duration: {route.Duration}s");

            if (route.Geometry != null)
            {
                LogAR($"First 10 geometry points:");
                for (int i = 0; i < Math.Min(10, route.Geometry.Count); i++)
                {
                    var point = route.Geometry[i];
                    LogAR($"    [{i}]: {point.x:F8}, {point.y:F8}");
                }
            }

            if (route.Legs != null && route.Legs.Count > 0)
            {
                var leg = route.Legs[0];
                LogAR($"  Steps in first leg: {leg.Steps?.Count ?? 0}");
                if (leg.Steps != null)
                {
                    for (int i = 0; i < Math.Min(5, leg.Steps.Count); i++)
                    {
                        var step = leg.Steps[i];
                        LogAR($"    Step {i}: {step.Maneuver?.Location.x:F8}, {step.Maneuver?.Location.y:F8}");
                    }
                }
            }
        }
        else
        {
            LogErrorAR("API call failed or returned no routes");
        }
    }
    public void SetNavigationMode(NavigationMode mode)
    {
        if (currentMode != mode)
        {
            currentMode = mode;
            routeProfile = GetRoutingProfile(mode);

            LogAR($"Navigation mode changed to: {mode}");
            UpdateStatus($"Mode: {mode} - Recalculating route...");

            // Trigger route recalculation
            RestartNavigation();
        }
    }

    private RoutingProfile GetRoutingProfile(NavigationMode mode)
    {
        switch (mode)
        {
            case NavigationMode.Walking: return RoutingProfile.Walking;
            case NavigationMode.Cycling: return RoutingProfile.Cycling;
            case NavigationMode.Driving: return RoutingProfile.Driving;
            default: return RoutingProfile.Walking;
        }
    }

    #endregion

    #region Utility Methods

    [ContextMenu("Debug API Response On-Screen")]
    public void DebugAPIResponseOnScreen()
    {
        StartCoroutine(TestAPIResponseOnScreen());
    }

    private IEnumerator TestAPIResponseOnScreen()
    {
        if (apiDebugText == null)
        {
            LogErrorAR("apiDebugText is not assigned in inspector!");
            yield break;
        }

        UpdateAPIDebugText("=== TESTING RAW API RESPONSE ===\nInitializing...");

        Vector2d[] waypoints = new Vector2d[] { currentPosition, destination };
        DirectionResource dr = new DirectionResource(waypoints, RoutingProfile.Walking);
        dr.Steps = true;
        dr.Overview = Mapbox.Directions.Overview.Full;

        UpdateAPIDebugText("API Request sent...\nWaiting for response...");

        bool completed = false;
        DirectionsResponse response = null;

        _directions.Query(dr, (DirectionsResponse res) =>
        {
            response = res;
            completed = true;
        });

        float timeout = 15f;
        float elapsed = 0f;

        while (!completed && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            UpdateAPIDebugText($"API Request sent...\nWaiting for response...\nElapsed: {elapsed:F1}s / {timeout}s");
            yield return null;
        }

        if (!completed)
        {
            UpdateAPIDebugText("API REQUEST TIMEOUT!\nNo response received after 15s\nCheck internet connection");
            yield break;
        }

        StringBuilder debugInfo = new StringBuilder();
        debugInfo.AppendLine("=== RAW API RESPONSE ===");

        if (response != null && response.Routes != null && response.Routes.Count > 0)
        {
            var route = response.Routes[0];
            debugInfo.AppendLine($"SUCCESS!");
            debugInfo.AppendLine($"Routes count: {response.Routes.Count}");
            debugInfo.AppendLine($"Geometry points: {route.Geometry?.Count ?? 0}");
            debugInfo.AppendLine($"Distance: {route.Distance}m");
            debugInfo.AppendLine($"Duration: {route.Duration}s");
            debugInfo.AppendLine("");

            if (route.Geometry != null && route.Geometry.Count > 0)
            {
                debugInfo.AppendLine($"First 5 geometry points:");
                for (int i = 0; i < Math.Min(5, route.Geometry.Count); i++)
                {
                    var point = route.Geometry[i];
                    debugInfo.AppendLine($"  [{i}]: {point.x:F6}, {point.y:F6}");
                }
                debugInfo.AppendLine("");
            }

            if (route.Legs != null && route.Legs.Count > 0)
            {
                var leg = route.Legs[0];
                debugInfo.AppendLine($"Steps in first leg: {leg.Steps?.Count ?? 0}");
                if (leg.Steps != null && leg.Steps.Count > 0)
                {
                    debugInfo.AppendLine("First 3 step locations:");
                    for (int i = 0; i < Math.Min(3, leg.Steps.Count); i++)
                    {
                        var step = leg.Steps[i];
                        if (step.Maneuver != null)
                        {
                            debugInfo.AppendLine($"  Step {i}: {step.Maneuver.Location.x:F6}, {step.Maneuver.Location.y:F6}");
                            debugInfo.AppendLine($"    Instruction: {step.Maneuver.Instruction ?? "No instruction"}");
                        }
                    }
                }
            }

            // Calculate actual distance between coordinates
            double straightDistance = CalculateDistanceBetweenPoints(currentPosition, destination);
            debugInfo.AppendLine("");
            debugInfo.AppendLine($"ANALYSIS:");
            debugInfo.AppendLine($"Straight-line distance: {straightDistance:F1}m");
            debugInfo.AppendLine($"Route distance: {route.Distance:F1}m");
            debugInfo.AppendLine($"Points per 100m: {(route.Geometry?.Count ?? 0) / (route.Distance / 100f):F1}");

            if ((route.Geometry?.Count ?? 0) < 5)
            {
                debugInfo.AppendLine("");
                debugInfo.AppendLine("WARNING: Very few geometry points!");
                debugInfo.AppendLine("This explains the arrow visibility issue.");
            }
        }
        else
        {
            debugInfo.AppendLine("API CALL FAILED!");
            debugInfo.AppendLine($"Response null: {response == null}");
            if (response != null)
            {
                debugInfo.AppendLine($"Routes null: {response.Routes == null}");
                debugInfo.AppendLine($"Routes count: {response.Routes?.Count ?? 0}");
            }
            debugInfo.AppendLine("Check:");
            debugInfo.AppendLine("- Internet connection");
            debugInfo.AppendLine("- Mapbox API key");
            debugInfo.AppendLine("- Coordinate validity");
        }

        UpdateAPIDebugText(debugInfo.ToString());
    }

    private void UpdateAPIDebugText(string text)
    {
        if (apiDebugText != null)
        {
            apiDebugText.text = text;
            if (debugPanel != null && !debugPanel.activeInHierarchy)
            {
                debugPanel.SetActive(true);
            }
        }
    }
    private void UpdateUserPosition()
    {
        if (arCamera != null)
            userARPosition = arCamera.transform.position;

        if (useDeviceGPS && Input.location.status == LocationServiceStatus.Running)
        {
            var location = Input.location.lastData;
            currentPosition = new Vector2d(location.latitude, location.longitude);
        }
    }
    private void ClearExistingArrows()
    {
        LogAR($"🧹 Clearing {arrowAnchors.Count} existing arrow anchors...");

        // By destroying the anchor's parent GameObject, we automatically destroy its child arrow.
        // This is the cleanest and most efficient way to clear everything.
        foreach (var anchor in arrowAnchors)
        {
            if (anchor != null && anchor.gameObject != null)
            {
                // FIX 1: Use Destroy() for safe, reliable runtime destruction.
                Destroy(anchor.gameObject);
            }
        }

        // FIX 2: Immediately clear all tracking lists to reset the state for the next run.
        spawnedArrows.Clear();
        arrowAnchors.Clear();
        activeArrows.Clear();

        // This flag is also reset in RequestNavigationRoute's "Master Reset",
        // but setting it here as well provides extra safety.
        arrowsSpawned = false;

        LogAR("✅ All lists cleared and objects marked for destruction.");
    }

    public void UpdateStatus(string status)
    {
        currentStatus = status;
        LogAR($"📊 STATUS: {status}");
        if (statusText != null)
            statusText.text = status;
    }
    // Enhanced debug display that shows current processing state
private void UpdateDebugDisplayEnhanced()
    {
        if (debugText != null)
        {
            StringBuilder debug = new StringBuilder();
            debug.AppendLine("=== AR NAV DEBUG V6.0 ===");
            debug.AppendLine($"Status: {currentStatus}");
            debug.AppendLine($"GPS: {(locationInitialized ? "Ready" : "Init...")}");
            debug.AppendLine($"Planes: {planesDetected} (Locked: {planeLocked})");
            debug.AppendLine("");

            // Route data
            debug.AppendLine("ROUTE DATA:");
            debug.AppendLine($"Raw Points: {rawRoutePoints?.Count ?? 0}");
            debug.AppendLine($"AR Positions: {routeARPositions?.Count ?? 0}");
            debug.AppendLine($"Selected: {selectedRouteIndices?.Count ?? 0}");
            debug.AppendLine($"Route Received: {routeReceived}");
            debug.AppendLine($"Route Requested: {routeRequested}");
            debug.AppendLine("");

            // Arrow data
            debug.AppendLine("ARROW DATA:");
            debug.AppendLine($"Active Arrows: {activeArrows?.Count ?? 0}");
            debug.AppendLine($"Managed Arrows: {managedArrowObjects?.Count ?? 0}");
            debug.AppendLine($"Spawned: {spawnedArrows?.Count ?? 0}");
            debug.AppendLine($"Arrows Spawned Flag: {arrowsSpawned}");
            debug.AppendLine("");

            // Settings
            debug.AppendLine("SETTINGS:");
            debug.AppendLine($"Mode: {(useTestRoute ? "TEST/INDOOR" : "OUTDOOR/REAL")}");
            debug.AppendLine($"Navigation: {currentMode}");
            debug.AppendLine($"Scale Factor: {indoorScaleFactor}");
            debug.AppendLine($"Spacing: {arrowSpacing}m");
            debug.AppendLine($"Max Arrows: {maxArrows}");
            debug.AppendLine("");

            // GPS coordinates
            debug.AppendLine("COORDINATES:");
            debug.AppendLine($"Current: {currentPosition.x:F6}, {currentPosition.y:F6}");
            debug.AppendLine($"Destination: {destination.x:F6}, {destination.y:F6}");

            // Calculate and show distance
            double distance = CalculateDistanceBetweenPoints(currentPosition, destination);
            debug.AppendLine($"Distance: {distance:F1}m");

            debugText.text = debug.ToString();
        }
    }
    private void UpdateDebugDisplay()
    {
        UpdateDebugDisplayEnhanced();
    }
    [ContextMenu("Show/Hide Debug Panel")]
    public void ToggleDebugPanel()
    {
        if (debugPanel != null)
        {
            debugPanel.SetActive(!debugPanel.activeInHierarchy);
        }
    }

    #endregion

    #region Public Methods & Context Menu Commands
    [ContextMenu("Test Route Selection Algorithm")]
    public void TestRouteSelectionAlgorithm()
    {
        LogAR("🧪 TESTING ROUTE SELECTION ALGORITHM...");

        // Generate test data
        List<Vector2d> testRoute = GenerateTestRoute(currentPosition, destination, 25); // Many points
        LogAR($"Generated {testRoute.Count} GPS points");

        // Convert to AR
        List<Vector3> testARPositions = new List<Vector3>();
        Vector2d testOrigin = testRoute[0];

        foreach (var gpsPoint in testRoute)
        {
            // Simulate GPS to AR conversion
            double latDiff = gpsPoint.x - testOrigin.x;
            double lonDiff = gpsPoint.y - testOrigin.y;

            double metersPerDegreeLat = 111320.0;
            double metersPerDegreeLon = 111320.0 * System.Math.Cos(testOrigin.x * System.Math.PI / 180.0);

            double xMeters = lonDiff * metersPerDegreeLon * indoorScaleFactor;
            double zMeters = latDiff * metersPerDegreeLat * indoorScaleFactor;

            testARPositions.Add(new Vector3((float)xMeters, 0f, (float)zMeters));
        }

        LogAR($"Converted to {testARPositions.Count} AR positions");

        // Test selection
        List<int> selected = SelectRoutePoints(testARPositions, arrowSpacing);
        LogAR($"Algorithm selected {selected.Count} points from {testARPositions.Count} candidates");

        // Show what would be spawned
        for (int i = 0; i < selected.Count; i++)
        {
            int idx = selected[i];
            Vector3 pos = testARPositions[idx];
            string type = i == 0 ? "🟢START" : (i == selected.Count - 1 ? "🔵END" : "🟡MID");
            LogAR($"   Would spawn: {type} at ({pos.x:F1}, {pos.z:F1})");
        }
    }
    [ContextMenu("Force Route Generation")]
    public void ForceRouteGeneration()
    {
        LogAR("🔧 FORCE: Manually triggering route generation...");
        routeRequested = false;
        routeReceived = false;
        arrowsSpawned = false;
        rawRoutePoints.Clear();
        routeARPositions.Clear();
        StartCoroutine(RequestNavigationRouteWithMode());
    }

    [ContextMenu("Force Plane Detection Check")]
    public void ForceCheckPlaneDetection()
    {
        LogAR("🔧 FORCE: Manually checking plane detection...");

        if (planeManager == null)
        {
            LogErrorAR("❌ ARPlaneManager is null!");
            return;
        }

        int totalPlanes = planeManager.trackables.count;
        LogAR($"📊 Current trackable planes: {totalPlanes}");

        if (totalPlanes == 0)
        {
            LogAR("⚠️ No planes detected yet. Make sure device is pointing at a flat surface.");
            UpdateStatus("No planes detected - point camera at ground");
            return;
        }

        foreach (var plane in planeManager.trackables)
        {
            float horizontality = Vector3.Dot(plane.transform.up, Vector3.up);
            float planeSize = plane.size.x * plane.size.y;
            bool suitable = IsSuitableForNavigation(plane);

            LogAR($"Plane check: horizontal={horizontality:F2}, size={planeSize:F2}, suitable={suitable}, tracking={plane.trackingState}");

            if (suitable && !planeLocked)
            {
                LogAR("🔧 FORCE: Found suitable plane, locking now...");
                primaryPlane = plane;
                planeLocked = true;
                arOriginWorldPos = plane.transform.position;
                arOriginWorldRot = plane.transform.rotation;
                referencePosition = currentPosition;
                StartCoroutine(SetupCompassAlignmentFixed());
                UpdateStatus("Force locked plane - setting up navigation...");
                if (scanningIndicator != null) scanningIndicator.SetActive(false);
                break;
            }
        }

        if (!planeLocked)
        {
            LogAR("❌ No suitable planes found for locking");
            UpdateStatus("No suitable planes - keep scanning ground");
        }
    }

    [ContextMenu("Force Lock Any Plane")]
    public void ForceLockAnyPlane()
    {
        LogAR("🔧 MANUALLY FORCING plane lock...");

        if (planeManager == null)
        {
            LogErrorAR("❌ ARPlaneManager is null!");
            return;
        }

        if (planeLocked)
        {
            LogAR("⚠️ Plane already locked, unlocking first...");
            planeLocked = false;
            primaryPlane = null;
        }

        int totalPlanes = planeManager.trackables.count;
        if (totalPlanes == 0)
        {
            LogErrorAR("❌ No planes available to lock!");
            return;
        }

        foreach (var plane in planeManager.trackables)
        {
            if (plane != null && plane.trackingState == TrackingState.Tracking)
            {
                LogAR($"🔧 FORCE LOCKING plane: size={plane.size.x:F2}x{plane.size.y:F2}");
                primaryPlane = plane;
                planeLocked = true;
                arOriginWorldPos = plane.transform.position;
                arOriginWorldRot = plane.transform.rotation;
                referencePosition = currentPosition;
                StartCoroutine(SetupCompassAlignmentFixed());
                UpdateStatus("Force locked plane - setting up navigation...");
                if (scanningIndicator != null) scanningIndicator.SetActive(false);
                break;
            }
        }

        if (!planeLocked)
        {
            LogErrorAR("❌ Could not force lock any plane - all planes invalid");
        }
    }

    [ContextMenu("Debug Arrow Prefab")]
    public void DebugArrowPrefab()
    {
        LogAR("=== DEBUGGING ARROW PREFAB ===");
        LogAR($"arrowPrefab assigned: {arrowPrefab != null}");

        if (arrowPrefab != null)
        {
            LogAR($"Prefab name: {arrowPrefab.name}");
            Renderer[] renderers = arrowPrefab.GetComponentsInChildren<Renderer>(true);
            LogAR($"Prefab has {renderers.Length} renderers");

            foreach (var renderer in renderers)
            {
                LogAR($"  Renderer: {renderer.name}, Enabled: {renderer.enabled}");
                LogAR($"  Material: {renderer.material?.name ?? "NULL"}");
            }

            LogAR("Testing prefab instantiation...");
            GameObject testArrow = Instantiate(arrowPrefab);
            if (testArrow != null)
            {
                testArrow.name = "TEST_ARROW";
                testArrow.transform.position = arCamera.transform.position + arCamera.transform.forward * 2f;
                testArrow.transform.localScale = Vector3.one * 2f;

                Renderer[] testRenderers = testArrow.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in testRenderers)
                {
                    renderer.material.color = Color.red;
                    renderer.enabled = true;
                }

                LogAR($"✅ Test arrow created at: {testArrow.transform.position}");
            }
            else
            {
                LogErrorAR("❌ Failed to instantiate test arrow!");
            }
        }
        else
        {
            LogErrorAR("❌ Arrow prefab is not assigned in inspector!");
        }
    }

    [ContextMenu("Test Arrow Near Camera")]
    public void TestArrowNearCamera()
    {
        LogAR("🧪 MANUAL TEST: Spawning single test arrow...");

        if (primaryPlane == null)
        {
            LogErrorAR("❌ No primary plane for testing");
            return;
        }

        if (arrowPrefab == null)
        {
            LogErrorAR("❌ No arrow prefab assigned");
            return;
        }

        ClearExistingArrows();

        Vector3 cameraPos = Camera.main.transform.position;
        Vector3 testPos = cameraPos + Camera.main.transform.forward * 2f;
        testPos.y = primaryPlane.transform.position.y + 0.1f;

        LogAR($"📍 Test arrow position: ({testPos.x:F2}, {testPos.y:F2}, {testPos.z:F2})");
        GameObject anchorObject = new GameObject("TestAnchor");
        anchorObject.transform.position = testPos;
        anchorObject.transform.rotation = Quaternion.identity;
        ARAnchor testAnchor = anchorObject.AddComponent<ARAnchor>();
        if (testAnchor != null)
        {
            GameObject testArrow = Instantiate(arrowPrefab, testAnchor.transform);
            testArrow.name = "TEST_Arrow";
            testArrow.transform.localPosition = Vector3.zero;
            testArrow.transform.localScale = Vector3.one * arrowScale;

            // Make it bright red for visibility
            Renderer[] renderers = testArrow.GetComponentsInChildren<Renderer>();
            foreach (var renderer in renderers)
            {
                renderer.enabled = true;
                renderer.material.color = Color.red;
            }

            testArrow.SetActive(true);
            activeArrows.Add(testArrow);
            arrowAnchors.Add(testAnchor);

            LogAR($"✅ Test arrow spawned: {testArrow.name}");
        }
        else
        {
            LogErrorAR("❌ Failed to create test anchor");
        }
    }

    public void RestartNavigation()
    {
        LogAR("🔄 Restarting navigation...");
        routeRequested = false;
        routeReceived = false;
        arrowsSpawned = false;
        primaryPlane = null;
        planeLocked = false;

        ClearExistingArrows();
        routeWorldPositions.Clear();
        routeARPositions.Clear();
        rawRoutePoints.Clear();
        selectedRouteIndices.Clear();

        UpdateStatus("Restarted - point camera at ground");
        if (scanningIndicator != null)
            scanningIndicator.SetActive(true);
    }

    public void UpdateDestination(Vector2d newDestination)
    {
        LogAR($"🎯 New destination: {newDestination.x:F6}, {newDestination.y:F6}");
        destination = newDestination;
        RestartNavigation();
    }

    public void SetDestination(double latitude, double longitude)
    {
        destination = new Vector2d(latitude, longitude);
        RestartNavigation();
    }

    public void ToggleTestMode()
    {
        useTestRoute = !useTestRoute;
        LogAR($"🧪 Test mode: {useTestRoute}");
        RestartNavigation();
    }

    public void AdjustArrowScale(float newScale)
    {
        arrowScale = Mathf.Clamp(newScale, 0.1f, 2f);
        foreach (var arrow in activeArrows)
        {
            if (arrow != null)
                arrow.transform.localScale = Vector3.one * arrowScale;
        }
    }

    public void ToggleShowAllArrows()
    {
        showAllArrows = !showAllArrows;
        LogAR($"👁️ Show all arrows: {showAllArrows}");

        if (showAllArrows)
        {
            foreach (var arrow in activeArrows)
            {
                if (arrow != null)
                    arrow.SetActive(true);
            }
        }
    }

    // Additional button methods for UI
    public void OnAutoCalibrateToDeviceGPSButton()
    {
        LogAR("🔘 Auto Calibrate to Device GPS pressed - Feature disabled for stability");
        UpdateStatus("Auto-calibration disabled for stability");
    }

    public void OnCalibrateButtonPressed()
    {
        LogAR("🔘 Manual Calibrate pressed - Feature disabled for stability");
        UpdateStatus("Manual calibration disabled for stability");
    }

    public void OnSetOriginFromScreenCenterButtonPressed()
    {
        LogAR("🔘 Set Origin pressed");
        StartCoroutine(SetOriginFromScreenCenterCoroutine());
    }

    private IEnumerator SetOriginFromScreenCenterCoroutine()
    {
        LogAR("🎯 Setting origin from screen center...");
        yield return null;

        Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        List<ARRaycastHit> hits = new List<ARRaycastHit>();

        if (raycastManager != null && raycastManager.Raycast(screenCenter, hits, TrackableType.PlaneWithinPolygon))
        {
            Pose hitPose = hits[0].pose;
            arOriginWorldPos = hitPose.position;
            arOriginWorldRot = hitPose.rotation;
            planeLocked = true;

            LogAR($"✅ Origin set to: {arOriginWorldPos}");
            UpdateStatus("Origin set from screen center");
        }
        else
        {
            if (arCamera != null)
            {
                arOriginWorldPos = arCamera.transform.position;
                arOriginWorldRot = arCamera.transform.rotation;
                LogAR($"⚠️ Fallback to camera position: {arOriginWorldPos}");
            }
        }

        if (routeReceived)
        {
            LogAR("🔄 Reprocessing route with new origin...");
            StartCoroutine(ProcessRouteData());
        }
    }
    public class ArrowMetadata : MonoBehaviour
    {
        public int RouteIndex { get; set; }
        public float DistanceFromStart { get; set; }
        public Vector3 StableWorldPosition { get; set; }
        public float LastUpdateTime { get; set; }
    }

    #endregion
}