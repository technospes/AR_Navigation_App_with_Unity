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
    [Header("Performance")]
    public int initialPoolSize = 30;
    private ArrowPool arrowPool;
    private float lastDynamicUpdateTime = 0f;
    private int framesSinceLastSpawn = 0;
    private Dictionary<int, float> arrowSpawnCooldowns = new Dictionary<int, float>();
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
    private bool isProcessingRoute = false;
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
    private List<Vector2d> rawRoutePoints = new List<Vector2d>();
    private List<int> selectedRouteIndices = new List<int>();

    // FIXED: Add missing spawnedArrows and arrowAnchors lists
    private List<GameObject> spawnedArrows = new List<GameObject>();
    private List<ARAnchor> arrowAnchors = new List<ARAnchor>();

    // GPS coordinates
    private Vector2d currentPosition = new Vector2d(29.907947712366155, 78.09645817912742);
    private Vector2d destination = new Vector2d(29.907585872781446, 78.0983731075453);
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
    private string currentStatus = "Initializing...";

    // Ground anchoring
    private List<StaticArrowData> staticArrows = new List<StaticArrowData>();

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
        arrowPool = new ArrowPool(arrowPrefab, initialPoolSize);
        lastUserPositionForUpdate = Vector3.zero;
        StartCoroutine(InitializeLocationServices());
    }

    void Update()
    {
        UpdateDebugDisplay();
        UpdateUserPosition();

        if (arrowsSpawned && routeWorldPositions.Count > 0)
        {
            float distanceMoved = Vector3.Distance(userARPosition, lastUserPositionForUpdate);

            // This block handles spawning NEW arrows as you move forward.
            if (distanceMoved > updateDistanceThreshold)
            {
                UpdateDynamicArrowSpawning();
                lastUserPositionForUpdate = userARPosition;
            }

            // This block calls your new method to clean up OLD arrows.
            if (Time.time - lastDynamicUpdateTime > 1.5f) // Runs every 1.5 seconds
            {
                UpdateArrowVisibilityAndCulling(); // <-- This is the call
                lastDynamicUpdateTime = Time.time;
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
        routeWorldPositions.Clear();
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
                // --- START OF FINAL FIX ---
                // FINAL SAFEGUARD: Manually densify the route if it's still too sparse.
                float pointDensity = (float)rawRoutePoints.Count / ((float)route.Distance / 100f); // Points per 100m

                // If density is low (e.g., less than 7 points per 100m, which is >15m spacing)
                if (pointDensity < 7.0f)
                {
                    // Force a point to exist at least every 15 meters.
                    rawRoutePoints = DensifyRoute(rawRoutePoints, arrowSpacing, totalRouteDistance, currentMode);
                }
                // --- END OF FINAL FIX ---
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
    private float CalculateTotalDistance(List<Vector2d> points)
    {
        if (points == null || points.Count < 2)
            return 0f;

        float totalDistance = 0f;
        for (int i = 1; i < points.Count; i++)
        {
            totalDistance += (float)CalculateDistanceBetweenPoints(points[i - 1], points[i]);
        }
        return totalDistance;
    }

    private void AdaptSettingsToRoute(float routeDistance, int pointCount)
    {
        LogAR("Adapting navigation settings to the current route...");

        // 1. Mode-based defaults
        switch (currentMode)
        {
            case NavigationMode.Walking:
                arrowSpacing = 6f;
                spawnAheadDistance = 80f;
                updateDistanceThreshold = 5f;
                break;
            case NavigationMode.Cycling:
                arrowSpacing = 15f;
                spawnAheadDistance = 120f;
                updateDistanceThreshold = 10f;
                break;
            case NavigationMode.Driving:
                arrowSpacing = 30f;
                spawnAheadDistance = 200f;
                updateDistanceThreshold = 20f;
                break;
        }

        // 2. Adjust for route length
        if (routeDistance < 300) // short route
        {
            arrowSpacing *= 0.8f; // denser arrows
        }
        else if (routeDistance > 5000) // long route 5km+
        {
            arrowSpacing *= 1.5f; // fewer arrows
            spawnAheadDistance *= 1.5f;
        }

        // 3. Cap for very long routes (e.g., 100km+)
        if (routeDistance > 50000)
        {
            arrowSpacing = Mathf.Clamp(routeDistance / 500f, 30f, 500f);
            spawnAheadDistance = Mathf.Clamp(routeDistance / 50f, 200f, 1000f);
        }

        // 4. Auto set other settings
        initialSpawnDistance = spawnAheadDistance;
        continuousSpawnAhead = spawnAheadDistance * 0.5f;
        arrowDespawnBehind = spawnAheadDistance * 0.2f;
        despawnBehindDistance = arrowDespawnBehind;
        arrowStabilityRadius = Mathf.Clamp(updateDistanceThreshold * 0.6f, 2f, 5f);
        maxVisibleDistance = spawnAheadDistance * 1.5f;

        maxArrows = Mathf.Clamp(Mathf.RoundToInt(routeDistance / arrowSpacing), 10, 300);
        minimumVisibleArrows = Mathf.Clamp(Mathf.RoundToInt(maxArrows * 0.2f), 5, 20);

        // 5. Sparse route fallback: force arrow per point
        if (pointCount <= 20)
        {
            LogAR("Sparse route detected - forcing arrow at each point");
            arrowSpacing = Mathf.Max(5f, arrowSpacing * 0.5f);
            minimumVisibleArrows = pointCount;
        }

        LogAR($"Settings adapted: Spacing={arrowSpacing:F1}m, Lookahead={spawnAheadDistance:F1}m, MaxArrows={maxArrows}, MinVisible={minimumVisibleArrows}");
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
    private void ValidateAllArrowVisibility()
    {
        if (managedArrowObjects.Count == 0) return;

        int visibleCount = 0;
        int invisibleCount = 0;

        foreach (var arrow in managedArrowObjects.ToList())
        {
            if (arrow == null)
            {
                managedArrowObjects.Remove(arrow);
                continue;
            }

            if (IsArrowVisible(arrow))
            {
                visibleCount++;
            }
            else
            {
                invisibleCount++;
                ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
                LogAR($"Arrow {metadata?.RouteIndex ?? -1} is invisible - attempting fix");
                AttemptArrowVisibilityFix(arrow);
            }
        }

        LogAR($"Visibility check: {visibleCount} visible, {invisibleCount} invisible");

        // If no arrows are visible but we should have some, trigger emergency spawn
        if (visibleCount == 0 && managedArrowObjects.Count > 0)
        {
            LogErrorAR("EMERGENCY: No arrows visible despite having managed arrows!");
            StartCoroutine(EmergencyArrowRespawn());
        }
    }
    private IEnumerator EmergencyArrowRespawn()
    {
        LogAR("=== EMERGENCY ARROW RESPAWN ===");

        // Clear everything and start fresh
        ClearExistingArrows();
        yield return new WaitForSeconds(0.1f);

        // Recalculate initial spawn
        SpawnInitialGuidanceArrows();

        LogAR("Emergency respawn complete");
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

        // Align route with AR world origin if plane is locked
        if (planeLocked && referencePosition != null)
        {
            LogAR("Using referencePosition as conversion origin to align route with AR world.");
            routeOrigin = referencePosition;
        }
        else
        {
            routeOrigin = rawRoutePoints[0];
        }

        routeWorldPositions.Clear();
        selectedRouteIndices.Clear();
        ClearExistingArrows();

        // Convert GPS → AR world positions
        for (int i = 0; i < rawRoutePoints.Count; i++)
        {
            Vector2d gpsPoint = rawRoutePoints[i];
            Vector3 worldPos = GPSToWorld(gpsPoint);
            routeWorldPositions.Add(worldPos);
        }

        // Optional: center route for indoor testing
        if (useTestRoute && routeWorldPositions.Count > 0)
        {
            CenterRouteForIndoorTesting();
        }

        // ⚡ Don't overwrite arrowSpacing here! Just log average distances
        LogAverageSegmentDistance();

        // Select arrow indices based on final arrowSpacing
        selectedRouteIndices = SelectRoutePointsImproved(routeWorldPositions, arrowSpacing);

        LogAR($"Route processed: {selectedRouteIndices.Count} arrow positions selected");
        UpdateStatus($"Route ready: {selectedRouteIndices.Count} arrows planned");

        // Initialize arrow system
        arrowsSpawned = true;
        lastUserPositionForUpdate = userARPosition;

        // Spawn arrows at selected indices
        SpawnInitialGuidanceArrows();

        LogAR("=== ROUTE PROCESSING COMPLETED ===");
    }

    private int CalculateAdaptiveArrowCount(float totalDistance, int baseCount)
    {
        // Adjust based on navigation mode
        float modeMultiplier = 1f;
        switch (currentMode)
        {
            case NavigationMode.Walking:
                modeMultiplier = 1.2f; // More arrows for walking
                break;
            case NavigationMode.Cycling:
                modeMultiplier = 1.0f; // Normal
                break;
            case NavigationMode.Driving:
                modeMultiplier = 0.8f; // Fewer arrows for driving
                break;
        }

        // Adjust based on route length
        float lengthMultiplier = 1f;
        if (totalDistance < 50f)
            lengthMultiplier = 1.5f; // More arrows for short routes
        else if (totalDistance > 200f)
            lengthMultiplier = 0.8f; // Fewer arrows for long routes

        int adaptiveCount = Mathf.RoundToInt(baseCount * modeMultiplier * lengthMultiplier);

        LogAR($"Adaptive count calculation: base={baseCount}, mode={modeMultiplier}, length={lengthMultiplier}, result={adaptiveCount}");

        return adaptiveCount;
    }
    private int FindBestIndexForDistance(List<float> distances, float targetDistance, List<int> excludeIndices)
    {
        float bestDiff = float.MaxValue;
        int bestIndex = -1;

        for (int i = 1; i < distances.Count - 1; i++)
        {
            if (excludeIndices.Contains(i)) continue;

            float diff = Mathf.Abs(distances[i] - targetDistance);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIndex = i;
            }
        }

        return bestIndex;
    }
    private void CenterRouteForIndoorTesting()
    {
        if (routeWorldPositions.Count == 0) return;

        // Calculate centroid
        Vector3 centroid = Vector3.zero;
        foreach (var pos in routeWorldPositions)
        {
            centroid += pos;
        }
        centroid /= routeWorldPositions.Count;
        centroid.y = 0f;

        // Position route in front of camera
        Vector3 cameraForwardOffset = arCamera.transform.forward * 3f;
        cameraForwardOffset.y = 0f;
        Quaternion cameraYawRot = Quaternion.Euler(0f, arCamera.transform.eulerAngles.y, 0f);

        // Reposition and rotate every point
        for (int i = 0; i < routeWorldPositions.Count; i++)
        {
            routeWorldPositions[i] -= centroid;
            routeWorldPositions[i] = cameraYawRot * routeWorldPositions[i];
            routeWorldPositions[i] += cameraForwardOffset;
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

        // Handle long route info (optional logging)
        if (!useTestRoute && rawRoutePoints.Count > maxPointsPerSegment)
        {
            LogAR($"Long route detected: {rawRoutePoints.Count} points, processing first {maxPointsPerSegment}");
        }

        // 1. Calculate total route distance first
        totalRouteDistance = CalculateTotalDistance(rawRoutePoints);

        // 2. Adapt navigation settings dynamically for this route
        AdaptSettingsToRoute(totalRouteDistance, rawRoutePoints.Count);

        // 3. Densify route points if large gaps exist
        rawRoutePoints = DensifyRoute(rawRoutePoints, arrowSpacing, totalRouteDistance, currentMode);

        // 4. Process the full route data (AR conversion + arrow planning)
        yield return StartCoroutine(ProcessRouteData());

        // 5. Calculate distances for dynamic arrow updates
        CalculateCumulativeDistances();

        LogAR($"Initial segment processed: {routeWorldPositions.Count} AR positions, {totalRouteDistance:F1}m total");
    }


    // FIXED: GPS to AR conversion
    // DELETE your old ConvertGPSToAR method and ADD this one.
    private Vector3 GPSToWorld(Vector2d gpsPoint)
    {
        // Use the same origin used for conversion (routeOrigin)
        double latDiff = gpsPoint.x - routeOrigin.x;
        double lonDiff = gpsPoint.y - routeOrigin.y;

        double metersPerDegreeLat = 111320.0;
        double metersPerDegreeLon = 111320.0 * Math.Cos(routeOrigin.x * Math.PI / 180.0);

        double xMeters = lonDiff * metersPerDegreeLon;
        double zMeters = latDiff * metersPerDegreeLat;

        // Apply indoor scaling correctly if in test mode
        if (useTestRoute)
        {
            xMeters *= indoorScaleFactor;
            zMeters *= indoorScaleFactor;
        }

        Vector3 localOffset = new Vector3((float)xMeters, 0f, (float)zMeters);

        // Apply northAlignment to orient the offset correctly
        localOffset = northAlignment * localOffset;

        // Add the offset to the AR World Origin to get the final world position
        return arOriginWorldPos + localOffset;
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
    routeWorldPositions.Clear();
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
        if (routeWorldPositions == null || routeWorldPositions.Count < 2 || primaryPlane == null)
        {
            return;
        }

        LogAR("=== DYNAMIC ARROW UPDATE START ===");

        // Calculate user progress
        UserProgressData progress = CalculateUserProgressEnhanced();
        LogAR($"User progress: {progress.distanceAlongRoute:F1}m along route, nearest index: {progress.nearestRouteIndex}");

        // Define spawn/despawn ranges
        float currentPos = progress.distanceAlongRoute;
        float spawnStart = Mathf.Max(0, currentPos - despawnBehindDistance);
        float spawnEnd = Mathf.Min(totalRouteDistance, currentPos + spawnAheadDistance);

        LogAR($"Spawn window: {spawnStart:F1}m to {spawnEnd:F1}m (current: {currentPos:F1}m)");

        // Determine required arrows (start from current route index to avoid adding old indices)
        int currentRouteIndex = GetCurrentRouteIndex();
        HashSet<int> requiredIndices = CalculateRequiredArrowIndices(spawnStart, spawnEnd, currentRouteIndex);
        LogAR($"Required arrows: {requiredIndices.Count} indices (currentIdx={currentRouteIndex})");

        // Remove arrows outside spawn range OR behind by route distance
        UpdateArrowVisibilityAndCulling();

        // Add new arrows in spawn range
        List<int> newIndices = FindMissingArrows(requiredIndices);

        if (newIndices.Count > 0)
        {
            LogAR($"Spawning {newIndices.Count} new arrows");
            StartCoroutine(SpawnArrowsFromIndicesWithValidation(newIndices));
        }
        else
        {
            LogAR("No new arrows needed");
        }

        LogAR($"Dynamic update complete: {managedArrowObjects.Count} total arrows");
    }
    private HashSet<int> CalculateRequiredArrowIndices(float spawnStart, float spawnEnd, int startIndex)
    {
        HashSet<int> requiredIndices = new HashSet<int>();

        // Always include start and end if they're in range
        if (cumulativeDistances[0] >= spawnStart && cumulativeDistances[0] <= spawnEnd)
            requiredIndices.Add(0);

        if (cumulativeDistances[cumulativeDistances.Count - 1] >= spawnStart &&
            cumulativeDistances[cumulativeDistances.Count - 1] <= spawnEnd)
            requiredIndices.Add(cumulativeDistances.Count - 1);

        // Add intermediate points based on spacing
        float lastIncludedDistance = spawnStart - arrowSpacing;

        for (int i = 1; i < routeWorldPositions.Count - 1; i++)
        {
            float routeDistance = cumulativeDistances[i];

            if (routeDistance >= spawnStart && routeDistance <= spawnEnd)
            {
                float distanceFromLast = routeDistance - lastIncludedDistance;

                if (distanceFromLast >= arrowSpacing)
                {
                    requiredIndices.Add(i);
                    lastIncludedDistance = routeDistance;
                    LogAR($"Including arrow at index {i}, distance {routeDistance:F1}m");
                }
            }
        }

        // Ensure minimum arrow count for guidance
        if (requiredIndices.Count < minimumVisibleArrows)
        {
            LogAR($"Only {requiredIndices.Count} arrows planned, adding more for minimum {minimumVisibleArrows}");

            // Add more arrows with reduced spacing
            float reducedSpacing = arrowSpacing * 0.7f;
            lastIncludedDistance = spawnStart - reducedSpacing;

            for (int i = 0; i < routeWorldPositions.Count && requiredIndices.Count < minimumVisibleArrows; i++)
            {
                float routeDistance = cumulativeDistances[i];

                if (routeDistance >= spawnStart && routeDistance <= spawnEnd)
                {
                    float distanceFromLast = routeDistance - lastIncludedDistance;

                    if (distanceFromLast >= reducedSpacing && !requiredIndices.Contains(i))
                    {
                        requiredIndices.Add(i);
                        lastIncludedDistance = routeDistance;
                        LogAR($"Added extra arrow at index {i} for minimum count");
                    }
                }
            }
        }

        return requiredIndices;
    }
    private IEnumerator SpawnArrowsFromIndicesWithValidation(List<int> indices)
    {
        LogAR($"=== SPAWNING {indices.Count} ARROWS WITH VALIDATION ===");

        yield return StartCoroutine(SpawnArrowsFromIndices(indices));

        // Additional validation after spawning
        yield return new WaitForSeconds(0.2f);

        int actuallyVisible = 0;
        foreach (var arrow in managedArrowObjects)
        {
            if (arrow != null && IsArrowVisible(arrow))
            {
                actuallyVisible++;
            }
        }

        LogAR($"Post-spawn validation: {actuallyVisible}/{managedArrowObjects.Count} arrows actually visible");

        if (actuallyVisible == 0 && managedArrowObjects.Count > 0)
        {
            LogErrorAR("CRITICAL: No arrows visible despite successful spawning!");
            DiagnoseSpawnIssues();
        }
    }
    private void DiagnoseSpawnIssues()
    {
        LogAR("=== DIAGNOSING SPAWN ISSUES ===");
        LogAR($"Primary plane: {primaryPlane != null}");
        LogAR($"Arrow prefab: {arrowPrefab != null}");
        LogAR($"Route positions: {routeWorldPositions?.Count ?? 0}");
        LogAR($"Selected indices: {selectedRouteIndices?.Count ?? 0}");
        LogAR($"Managed arrows: {managedArrowObjects?.Count ?? 0}");

        if (primaryPlane != null)
        {
            LogAR($"Primary plane position: {primaryPlane.transform.position}");
            LogAR($"Primary plane tracking state: {primaryPlane.trackingState}");
        }

        if (arrowPrefab != null)
        {
            Renderer[] prefabRenderers = arrowPrefab.GetComponentsInChildren<Renderer>(true);
            LogAR($"Arrow prefab has {prefabRenderers.Length} renderers");
        }

        // Check individual arrows
        for (int i = 0; i < managedArrowObjects.Count && i < 5; i++)
        {
            var arrow = managedArrowObjects[i];
            if (arrow != null)
            {
                ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
                Vector3 screenPos = arCamera.WorldToScreenPoint(arrow.transform.position);
                float distance = Vector3.Distance(arrow.transform.position, arCamera.transform.position);

                LogAR($"Arrow {i} (route {metadata?.RouteIndex ?? -1}): " +
                      $"active={arrow.activeInHierarchy}, " +
                      $"screen=({screenPos.x:F0},{screenPos.y:F0}), " +
                      $"distance={distance:F1}m");
            }
        }
    }
    private List<int> FindMissingArrows(HashSet<int> requiredIndices)
    {
        List<int> missingIndices = new List<int>();

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
                missingIndices.Add(index);
                LogAR($"Missing arrow at index {index}");
            }
        }

        return missingIndices;
    }

    // Helper method to get current route progress
    private int GetCurrentRouteIndex()
    {
        if (routeWorldPositions.Count == 0) return 0;

        float closestDistance = float.MaxValue;
        int closestIndex = 0;

        for (int i = 0; i < routeWorldPositions.Count; i++)
        {
            float distance = Vector3.Distance(userARPosition, routeWorldPositions[i]);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestIndex = i;
            }
        }

        return closestIndex;
    }

    // Improved complete arrow removal
    private void UpdateArrowVisibilityAndCulling()
    {
        if (managedArrowObjects.Count == 0) return;

        int currentRouteIndex = GetCurrentRouteIndex();

        // Iterate backwards because we are removing items from the list as we go
        for (int i = managedArrowObjects.Count - 1; i >= 0; i--)
        {
            GameObject arrow = managedArrowObjects[i];
            if (arrow == null)
            {
                managedArrowObjects.RemoveAt(i);
                continue;
            }

            ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
            if (metadata == null) continue;

            // --- CULLING LOGIC ---
            // An arrow is culled if it is far behind your progress on the route
            bool isFarBehind = (currentRouteIndex > metadata.RouteIndex + 3);
            float distanceToUser = Vector3.Distance(userARPosition, arrow.transform.position);

            if (isFarBehind && distanceToUser > despawnBehindDistance)
            {
                LogAR($"Culling arrow {metadata.RouteIndex} (Reason: Far behind user).");

                // Keep a reference to the anchor before doing anything else
                Transform anchorTransform = arrow.transform.parent;

                // Remove from all tracking lists
                managedArrowObjects.RemoveAt(i);
                activeArrows.Remove(arrow);
                spawnedArrows.Remove(arrow);

                // Return the arrow GameObject to the pool for reuse
                arrowPool.Return(arrow);

                // Now, destroy the old ARAnchor GameObject
                if (anchorTransform != null)
                {
                    ARAnchor anchor = anchorTransform.GetComponent<ARAnchor>();
                    if (anchor != null) arrowAnchors.Remove(anchor);
                    Destroy(anchorTransform.gameObject);
                }
            }
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

    // Replace your current method with this robust version
    // Replace your current method with this robust version
    private IEnumerator RequestNavigationRouteWithMode()
    {
        LogAR("--- RUNNING SCRIPT VERSION 9.0 ---");
        // This "gatekeeper" check MUST be the very first thing.
        if (isProcessingRoute)
        {
            LogAR("Already processing a route, ignoring new request.");
            yield break; // Exit immediately
        }
        isProcessingRoute = true;

        // This block guarantees the flag is always reset, even if an error occurs.
        try
        {
            LogAR("=== ROUTE REQUEST WITH NAVIGATION MODE ===");
            ClearExistingArrows();
            rawRoutePoints.Clear();
            routeWorldPositions.Clear();
            routeReceived = false;
            arrowsSpawned = false;
            routeRequested = true;

            UpdateStatus($"Getting {currentMode} route...");
            bool success = false;

            if (useTestRoute)
            {
                int pointCount = GetPointCountForMode(currentMode);
                rawRoutePoints = GenerateTestRouteForMode(currentPosition, destination, pointCount, currentMode);
                if (rawRoutePoints != null && rawRoutePoints.Count > 0)
                {
                    routeReceived = true;
                    success = true;
                }
            }
            else
            {
                yield return StartCoroutine(RequestRealRouteWithProfile());
                success = routeReceived;
            }

            if (success)
            {
                yield return StartCoroutine(ProcessInitialSegment());
            }
            else
            {
                UpdateStatus("Failed to get route.");
                routeRequested = false; // Allow retrying
            }
        }
        finally
        {
            // This block will ALWAYS execute, ensuring the gate is unlocked for the next request.
            isProcessingRoute = false;
        }
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
    // Add this new helper method to your script
    private List<Vector2d> DensifyRoute(List<Vector2d> sparsePoints, float baseSpacing, float routeDistance,NavigationMode mode)
    {
        if (sparsePoints == null || sparsePoints.Count < 2)
            return sparsePoints;

        // 1. Adjust spacing dynamically based on mode and route distance
        float desiredSpacing = baseSpacing;
        if (mode == NavigationMode.Walking) desiredSpacing = Mathf.Clamp(baseSpacing, 4f, 10f);
        else if (mode == NavigationMode.Cycling) desiredSpacing = Mathf.Clamp(baseSpacing * 2f, 10f, 25f);
        else if (mode == NavigationMode.Driving) desiredSpacing = Mathf.Clamp(baseSpacing * 4f, 20f, 50f);

        // Very long route? Increase spacing to reduce point count
        if (routeDistance > 5000) desiredSpacing *= 1.5f;
        if (routeDistance > 50000) desiredSpacing *= 2.5f;

        LogAR($"Densifying route: {sparsePoints.Count} → Target spacing: {desiredSpacing}m | Route length: {routeDistance:F1}m | Mode: {mode}");

        List<Vector2d> densePoints = new List<Vector2d>();
        densePoints.Add(sparsePoints[0]);

        int maxPointsPerSegment = 50; // Cap per segment to prevent explosion

        for (int i = 0; i < sparsePoints.Count - 1; i++)
        {
            Vector2d p1 = sparsePoints[i];
            Vector2d p2 = sparsePoints[i + 1];
            double segmentDistance = CalculateDistanceBetweenPoints(p1, p2);

            if (segmentDistance > desiredSpacing)
            {
                int pointsToInsert = Mathf.Min(Mathf.FloorToInt((float)segmentDistance / desiredSpacing), maxPointsPerSegment);

                for (int j = 1; j <= pointsToInsert; j++)
                {
                    double t = (double)j / (pointsToInsert + 1);
                    Vector2d interpolatedPoint = new Vector2d(
                        p1.x + (p2.x - p1.x) * t,
                        p1.y + (p2.y - p1.y) * t
                    );
                    densePoints.Add(interpolatedPoint);
                }
            }

            densePoints.Add(p2);
        }

        LogAR($"Densification complete: {densePoints.Count} points after processing");
        return densePoints;
    }
    private UserProgressData CalculateUserProgressEnhanced()
    {
        UserProgressData progress = new UserProgressData();
        float closestDistance = float.MaxValue;
        int bestSegmentIndex = 0;

        Vector3 userPosFlat = new Vector3(userARPosition.x, 0, userARPosition.z);

        // Check each route segment
        for (int i = 0; i < routeWorldPositions.Count - 1; i++)
        {
            Vector3 segmentStart = new Vector3(routeWorldPositions[i].x, 0, routeWorldPositions[i].z);
            Vector3 segmentEnd = new Vector3(routeWorldPositions[i + 1].x, 0, routeWorldPositions[i + 1].z);

            Vector3 closestPoint = FindNearestPointOnLine(segmentStart, segmentEnd, userPosFlat);
            float distance = Vector3.Distance(userPosFlat, closestPoint);

            if (distance < closestDistance)
            {
                closestDistance = distance;
                bestSegmentIndex = i;
                progress.nearestRouteIndex = i;
                progress.projectedPosition = closestPoint;
                progress.distanceFromRoute = distance;

                // Calculate progress along this segment
                float segmentProgress = Vector3.Distance(segmentStart, closestPoint) /
                                      Vector3.Distance(segmentStart, segmentEnd);
                segmentProgress = Mathf.Clamp01(segmentProgress);

                progress.distanceAlongRoute = cumulativeDistances[i] +
                    (cumulativeDistances[i + 1] - cumulativeDistances[i]) * segmentProgress;
            }
        }

        return progress;
    }
    private void RemoveArrowSafely(GameObject arrow)
    {
        if (arrow == null) return;

        ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
        int routeIndex = metadata != null ? metadata.RouteIndex : -1;

        // FIXED: Remove from ALL tracking lists consistently
        managedArrowObjects.Remove(arrow);
        activeArrows.Remove(arrow);
        spawnedArrows.Remove(arrow);

        // Find and remove anchor
        Transform anchorTransform = arrow.transform.parent;
        if (anchorTransform != null)
        {
            ARAnchor anchor = anchorTransform.GetComponent<ARAnchor>();
            if (anchor != null)
            {
                arrowAnchors.Remove(anchor);
                LogAR($"Removed anchor for arrow {routeIndex}");
            }

            Destroy(anchorTransform.gameObject);
        }
        else
        {
            Destroy(arrow);
        }

        LogAR($"Safely removed arrow {routeIndex} from all tracking lists");
    }

    [ContextMenu("Test Enhanced Dynamic Spawning")]
    public void TestEnhancedDynamicSpawning()
    {
        LogAR("=== TESTING ENHANCED DYNAMIC SPAWNING ===");

        if (!planeLocked)
        {
            LogErrorAR("No plane locked - cannot test spawning");
            return;
        }

        if (routeWorldPositions.Count == 0)
        {
            LogErrorAR("No route data - generate route first");
            return;
        }

        // Clear existing arrows
        ClearExistingArrows();

        // Test the new spawning system
        SpawnInitialGuidanceArrows();

        LogAR("Enhanced spawning test initiated");
    }
    [ContextMenu("Force Show All Route Arrows")]
    public void ForceShowAllRouteArrows()
    {
        LogAR("=== FORCE SHOWING ALL ROUTE ARROWS ===");

        if (routeWorldPositions.Count == 0)
        {
            LogErrorAR("No route data available");
            return;
        }

        ClearExistingArrows();

        // Create indices for ALL route points (for debugging)
        List<int> allIndices = new List<int>();
        for (int i = 0; i < routeWorldPositions.Count; i++)
        {
            allIndices.Add(i);
        }

        // Limit to maxArrows to prevent overload
        if (allIndices.Count > maxArrows)
        {
            allIndices = allIndices.Take(maxArrows).ToList();
        }

        LogAR($"Force spawning {allIndices.Count} arrows");
        StartCoroutine(SpawnArrowsFromIndicesWithValidation(allIndices));
    }
    private Vector3 CalculateArrowDirection(int routeIndex)
    {
        Vector3 direction = arCamera.transform.forward; // Fallback

        if (routeIndex < routeWorldPositions.Count - 1)
        {
            Vector3 current = routeWorldPositions[routeIndex];
            Vector3 next = routeWorldPositions[routeIndex + 1];

            direction = (next - current).normalized;
            direction.y = 0f; // Keep horizontal

            if (direction.magnitude < 0.1f)
            {
                // Look ahead further if points are too close
                for (int i = routeIndex + 2; i < Mathf.Min(routeIndex + 5, routeWorldPositions.Count); i++)
                {
                    Vector3 futurePoint = routeWorldPositions[i];
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
    private void LogAverageSegmentDistance()
    {
        if (routeWorldPositions == null || routeWorldPositions.Count < 2) return;

        float totalDist = 0f;
        int segmentCount = 0;

        for (int i = 1; i < routeWorldPositions.Count; i++)
        {
            float segmentDist = Vector3.Distance(routeWorldPositions[i], routeWorldPositions[i - 1]);
            totalDist += segmentDist;
            segmentCount++;
        }

        if (segmentCount > 0)
        {
            float avgDist = totalDist / segmentCount;
            LogAR($"Average segment distance after densification: {avgDist:F1}m");
        }
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
        LogAR("=== ENHANCED INITIAL ARROW SPAWNING ===");

        if (routeWorldPositions.Count == 0)
        {
            LogErrorAR("No route positions available for arrow spawning");
            return;
        }

        // Calculate cumulative distances
        CalculateCumulativeDistances();

        // Clear existing arrows
        ClearExistingArrows();

        // Calculate initial spawn indices with better logic
        List<int> initialIndices = CalculateInitialSpawnIndices();

        LogAR($"Initial spawn plan: {initialIndices.Count} arrows planned for distances:");
        foreach (int index in initialIndices)
        {
            LogAR($"  Index {index}: {cumulativeDistances[index]:F1}m from start");
        }

        // Spawn arrows
        if (initialIndices.Count > 0)
        {
            StartCoroutine(SpawnArrowsFromIndicesWithValidation(initialIndices));
            arrowsSpawned = true;
        }
        else
        {
            LogErrorAR("No initial indices calculated - check route processing");
            arrowsSpawned = false;
        }
    }
    private IEnumerator SpawnArrowsFromIndices(List<int> indices)
    {
        LogAR($"=== SPAWNING ARROWS FROM {indices.Count} INDICES ===");
        if (primaryPlane == null || arrowPrefab == null)
        {
            LogErrorAR("Missing components (Plane or Prefab) - cannot spawn arrows");
            yield break;
        }

        List<GameObject> newlySpawnedArrows = new List<GameObject>();

        // Track last spawned position to prevent overlap
        Vector3? lastSpawnedPos = null;

        foreach (int routeIndex in indices)
        {
            if (routeIndex >= routeWorldPositions.Count)
            {
                LogErrorAR($"Invalid route index: {routeIndex}");
                continue;
            }

            // Prevent duplicates
            if (managedArrowObjects.Any(a => a != null && a.GetComponent<ArrowMetadata>()?.RouteIndex == routeIndex))
            {
                LogAR($"Arrow {routeIndex} already exists, skipping.");
                continue;
            }

            try
            {
                // Step 1: Get stable position with raycast + fallback
                Vector3 worldPosition = CalculateOptimalArrowPosition(routeIndex);

                // --- Overlap Prevention ---
                if (lastSpawnedPos.HasValue &&
                    Vector3.Distance(lastSpawnedPos.Value, worldPosition) < arrowSpacing * 0.8f)
                {
                    LogAR($"Skipping arrow {routeIndex} due to overlap (< {arrowSpacing * 0.8f}m).");
                    continue;
                }
                int nextIdx = Mathf.Min(routeIndex + 1, routeWorldPositions.Count - 1);
                // Flatten the direction
                Vector3 dir = routeWorldPositions[nextIdx] - routeWorldPositions[routeIndex];
                dir = Vector3.ProjectOnPlane(dir, Vector3.up);
                if (dir.sqrMagnitude < 0.01f)
                    dir = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up);

                // Get rotation along route
                Quaternion look = Quaternion.LookRotation(dir.normalized, Vector3.up);

                // Rotate prefab's up → forward so it's flat
                Quaternion prefabCorrection = Quaternion.FromToRotation(Vector3.up, Vector3.forward);

                // Final rotation
                Quaternion arrowRotation = look * prefabCorrection;


                // Step 3: Create stable anchor (new method)
                ARAnchor anchor = CreateStableAnchor(worldPosition, arrowRotation);
                if (anchor == null)
                {
                    LogErrorAR($"Failed to create stable anchor for arrow {routeIndex}");
                    continue;
                }
                // Step 4: Instantiate arrow prefab as child of anchor
                GameObject arrow = arrowPool.Get(); // Get an arrow from the pool
                arrow.transform.SetParent(anchor.transform);
                arrow.transform.localPosition = Vector3.zero;
                arrow.transform.localRotation = Quaternion.identity;
                arrow.SetActive(true);

                // Step 5: Setup arrow appearance + metadata
                SetupArrowAppearance(arrow, routeIndex);
                SetupArrowMetadata(arrow, routeIndex, worldPosition, routeWorldPositions[routeIndex]);

                // Step 6: Add to all tracking lists (fixes "Active Arrows = 0" issue)
                managedArrowObjects.Add(arrow);
                activeArrows.Add(arrow);
                spawnedArrows.Add(arrow);
                arrowAnchors.Add(anchor);
                newlySpawnedArrows.Add(arrow);

                // Track last spawned arrow for overlap checks
                lastSpawnedPos = worldPosition;

                LogAR($"✅ Arrow {routeIndex} spawned successfully.");
            }
            catch (System.Exception ex)
            {
                LogErrorAR($"Exception spawning arrow {routeIndex}: {ex.Message}");
            }

            // Spawn throttle: smoother visuals
            yield return new WaitForSeconds(0.02f);
        }

        // Post-spawn validation
        yield return new WaitForSeconds(0.1f);
        ValidateSpawnedArrows(newlySpawnedArrows);

        LogAR($"✅ Arrow spawning complete: {newlySpawnedArrows.Count}/{indices.Count} new arrows spawned.");
        UpdateStatus($"Navigation ready: {managedArrowObjects.Count} arrows visible");
    }

    // Replace your existing method with this smarter version
    private List<int> CalculateInitialSpawnIndices()
    {
        List<int> indices = new List<int>();
        if (routeWorldPositions.Count < 2 || cumulativeDistances.Count < 2)
        {
            LogErrorAR("Not enough data to calculate initial indices.");
            return indices;
        }

        indices.Add(0); // Always include the start point

        float targetDistance = arrowSpacing; // The next distance we want an arrow at

        // "Walk" along the route until we have enough arrows or have covered the initial distance
        while (targetDistance < initialSpawnDistance && indices.Count < maxArrows)
        {
            int bestIndex = -1;
            float smallestDiff = float.MaxValue;

            // Find the real route point that is closest to our ideal target distance
            for (int i = 1; i < cumulativeDistances.Count; i++)
            {
                float diff = Mathf.Abs(cumulativeDistances[i] - targetDistance);
                if (diff < smallestDiff)
                {
                    smallestDiff = diff;
                    bestIndex = i;
                }
            }

            // Add the index if it's new and valid
            if (bestIndex != -1 && !indices.Contains(bestIndex))
            {
                indices.Add(bestIndex);
            }

            // Set the next ideal target distance
            targetDistance += arrowSpacing;
        }
        indices.Sort(); // Ensure the indices are in the correct order
        LogAR($"Initial spawn plan calculated: {indices.Count} arrows.");
        return indices;
    }
    private void ValidateSpawnedArrows(List<GameObject> newArrows)
    {
        LogAR($"=== VALIDATING {newArrows.Count} NEWLY SPAWNED ARROWS ===");

        int validArrows = 0;
        int invisibleArrows = 0;

        foreach (var arrow in newArrows)
        {
            if (arrow == null)
            {
                LogErrorAR("Null arrow found in validation");
                continue;
            }

            // Check if arrow is actually visible
            bool isVisible = IsArrowVisible(arrow);
            ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();

            if (isVisible)
            {
                validArrows++;
                LogAR($"✅ Arrow {metadata?.RouteIndex ?? -1} validated as visible");
            }
            else
            {
                invisibleArrows++;
                LogErrorAR($"❌ Arrow {metadata?.RouteIndex ?? -1} spawned but not visible!");

                // Attempt to fix invisible arrow
                AttemptArrowVisibilityFix(arrow);
            }
        }

        LogAR($"Validation complete: {validArrows} visible, {invisibleArrows} invisible");

        if (invisibleArrows > validArrows)
        {
            LogErrorAR("Critical: More invisible arrows than visible - checking spawn system");
            DiagnoseSpawnIssues();
        }
    }
    private bool IsArrowVisible(GameObject arrow)
    {
        if (arrow == null || !arrow.activeInHierarchy) return false;

        Renderer[] renderers = arrow.GetComponentsInChildren<Renderer>();
        bool hasEnabledRenderer = false;

        foreach (var renderer in renderers)
        {
            if (renderer != null && renderer.enabled && renderer.isVisible)
            {
                hasEnabledRenderer = true;
                break;
            }
        }

        // Check if arrow is within camera view
        Vector3 screenPos = arCamera.WorldToScreenPoint(arrow.transform.position);
        bool inScreen = screenPos.x >= 0 && screenPos.x <= Screen.width &&
                       screenPos.y >= 0 && screenPos.y <= Screen.height && screenPos.z > 0;

        // Check distance from camera
        float distanceFromCamera = Vector3.Distance(arrow.transform.position, arCamera.transform.position);
        bool reasonableDistance = distanceFromCamera > 0.1f && distanceFromCamera < maxVisibleDistance;

        return hasEnabledRenderer && inScreen && reasonableDistance;
    }
    private void AttemptArrowVisibilityFix(GameObject arrow)
    {
        if (arrow == null) return;

        LogAR($"Attempting to fix invisible arrow: {arrow.name}");

        // Force enable all renderers
        Renderer[] renderers = arrow.GetComponentsInChildren<Renderer>();
        foreach (var renderer in renderers)
        {
            if (renderer != null)
            {
                renderer.enabled = true;

                // Ensure material is properly set
                if (renderer.material != null)
                {
                    renderer.material.color = Color.red; // High visibility color for debugging
                }
            }
        }

        // Force activate the GameObject
        arrow.SetActive(true);

        // Adjust position if too close to camera
        float distanceFromCamera = Vector3.Distance(arrow.transform.position, arCamera.transform.position);
        if (distanceFromCamera < 1f)
        {
            Vector3 newPos = arCamera.transform.position + arCamera.transform.forward * 2f;
            newPos.y = primaryPlane.transform.position.y + arrowHeightOffset;
            arrow.transform.position = newPos;
            LogAR($"Moved arrow away from camera to: {newPos}");
        }
    }

    private void SetupArrowMetadata(GameObject arrow, int routeIndex, Vector3 worldPosition, Vector3 arPosition)
    {
        ArrowMetadata metadata = arrow.GetComponent<ArrowMetadata>();
        if (metadata == null)
        {
            metadata = arrow.AddComponent<ArrowMetadata>();
        }

        metadata.RouteIndex = routeIndex;
        metadata.DistanceFromStart = cumulativeDistances[routeIndex];
        metadata.StableWorldPosition = worldPosition;
        metadata.OriginalARPosition = arPosition;
        metadata.LastUpdateTime = Time.time;
        metadata.SpawnDistance = Vector3.Distance(userARPosition, worldPosition);

        LogAR($"Metadata set for arrow {routeIndex}: distance={metadata.DistanceFromStart:F1}m, spawn distance={metadata.SpawnDistance:F1}m");
    }
    private Vector3 CalculateOptimalArrowPosition(int routeIndex)
    {
        // Step 1: Convert GPS to world space
        Vector3 worldPosition = GPSToWorld(rawRoutePoints[routeIndex]);

        // FIXED: More robust raycast approach for Y positioning
        Vector3 raycastOrigin = worldPosition;
        raycastOrigin.y = arCamera.transform.position.y; // Start from camera height

        List<ARRaycastHit> hits = new List<ARRaycastHit>();

        Ray ray = new Ray(raycastOrigin, Vector3.down);

        // Now, call Raycast with the Ray object and the hit list
        if (raycastManager.Raycast(ray, hits, TrackableType.Planes))
        {
            worldPosition.y = hits[0].pose.position.y + arrowHeightOffset;
            LogAR($"Direct raycast successful for arrow {routeIndex}");
        }
        // Fallback to screen-based raycast
        else
        {
            Vector3 screenPoint = arCamera.WorldToScreenPoint(worldPosition);
            if (screenPoint.x >= 0 && screenPoint.x <= Screen.width &&
                screenPoint.y >= 0 && screenPoint.y <= Screen.height && screenPoint.z > 0)
            {
                if (raycastManager.Raycast(new Vector2(screenPoint.x, screenPoint.y), hits, TrackableType.Planes))
                {
                    worldPosition.y = hits[0].pose.position.y + arrowHeightOffset;
                    LogAR($"Screen raycast successful for arrow {routeIndex}");
                }
                else
                {
                    // FIXED: More stable fallback
                    worldPosition.y = primaryPlane.transform.position.y + arrowHeightOffset;
                    LogAR($"Using stable plane Y for arrow {routeIndex}: {worldPosition.y}");
                }
            }
            else
            {
                worldPosition.y = primaryPlane.transform.position.y + arrowHeightOffset;
            }
        }

        return worldPosition;
    }

    // FIXED: Improve anchor creation for stability
    private ARAnchor CreateStableAnchor(Vector3 worldPosition, Quaternion rotation)
    {
        try
        {
            List<ARRaycastHit> hits = new List<ARRaycastHit>();
            Vector3 rayOrigin = new Vector3(worldPosition.x, arCamera.transform.position.y, worldPosition.z);

            // Step 1: Raycast to find an AR plane under the point
            if (raycastManager.Raycast(new Ray(rayOrigin, Vector3.down), hits, TrackableType.Planes))
            {
                ARPlane plane = planeManager.GetPlane(hits[0].trackableId);
                if (plane != null)
                {
                    Pose planePose = new Pose(hits[0].pose.position, rotation);
                    ARAnchor planeAnchor = anchorManager.AttachAnchor(plane, planePose); // ✅ Recommended method
                    if (planeAnchor != null)
                    {
                        LogAR($"Stable anchor attached to plane at: {planePose.position}");
                        return planeAnchor;
                    }
                }
            }

            // Step 2: Fallback if no plane → create empty GameObject with ARAnchor
            GameObject fallbackGO = new GameObject("WorldAnchor");
            fallbackGO.transform.position = worldPosition;
            fallbackGO.transform.rotation = rotation;

            ARAnchor fallbackAnchor = fallbackGO.AddComponent<ARAnchor>();
            LogAR($"Fallback anchor created at: {worldPosition}");
            return fallbackAnchor;
        }
        catch (System.Exception ex)
        {
            LogErrorAR($"Exception creating anchor: {ex.Message}");
            return null;
        }
    }
    private void SetupArrowAppearance(GameObject arrow, int routeIndex)
    {
        arrow.name = $"NavArrow_{routeIndex}_{Time.time:F0}";
        arrow.transform.localPosition = Vector3.zero;
        arrow.transform.localScale = Vector3.one * arrowScale;

        // FIXED: Determine color with clear hierarchy
        Color arrowColor = DetermineArrowColorFixed(routeIndex);
        LogAR($"Arrow {routeIndex} assigned color: {arrowColor}");

        // FIXED: Apply color and ensure visibility with material instancing
        Renderer[] renderers = arrow.GetComponentsInChildren<Renderer>();
        bool hasVisibleRenderer = false;

        foreach (var renderer in renderers)
        {
            if (renderer != null)
            {
                renderer.enabled = true;

                // CRITICAL FIX: Create new material instance to prevent shared material conflicts
                Material[] materials = new Material[renderer.materials.Length];
                for (int i = 0; i < renderer.materials.Length; i++)
                {
                    materials[i] = new Material(renderer.materials[i]);
                    materials[i].color = arrowColor;

                    // Force material properties for visibility
                    if (materials[i].HasProperty("_Color"))
                        materials[i].SetColor("_Color", arrowColor);
                    if (materials[i].HasProperty("_BaseColor"))
                        materials[i].SetColor("_BaseColor", arrowColor);
                }
                renderer.materials = materials;

                hasVisibleRenderer = true;
            }
        }

        if (!hasVisibleRenderer)
        {
            LogErrorAR($"Arrow {routeIndex} has no visible renderers!");
        }

        // Ensure arrow is active
        arrow.SetActive(true);
    }

    private Color DetermineArrowColorFixed(int routeIndex)
    {
        // Clear color hierarchy to prevent mixing
        if (routeIndex == 0)
            return new Color(0f, 1f, 0f, 1f);    // Pure green for start
        else if (routeIndex >= routeWorldPositions.Count - 1)
            return new Color(0f, 0f, 1f, 1f);    // Pure blue for end
        else if (routeIndex <= 3)
            return new Color(0f, 1f, 1f, 1f);    // Pure cyan for near start
        else
            return new Color(1f, 1f, 0f, 1f);    // Pure yellow for middle
    }
    private void CalculateCumulativeDistances()
    {
        cumulativeDistances.Clear();
        cumulativeDistances.Add(0f);
        totalRouteDistance = 0f;

        for (int i = 1; i < routeWorldPositions.Count; i++)
        {
            float segmentDistance = Vector3.Distance(routeWorldPositions[i], routeWorldPositions[i - 1]);
            totalRouteDistance += segmentDistance;
            cumulativeDistances.Add(totalRouteDistance);
        }

        LogAR($"Route analysis: Total distance {totalRouteDistance:F1}m across {routeWorldPositions.Count} points");
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
            debug.AppendLine($"AR Positions: {routeWorldPositions?.Count ?? 0}");
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
        routeWorldPositions.Clear();
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
        routeWorldPositions.Clear();
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
        public bool IsInitiallyVisible { get; set; } = true;
        public float SpawnDistance { get; set; } // Distance from user when spawned
        public Vector3 OriginalARPosition { get; set; } // Original AR space position
    }

    #endregion
}