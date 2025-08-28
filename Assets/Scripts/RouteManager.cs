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

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class RouteManager : MonoBehaviour
{
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
    private Vector2d currentPosition = new Vector2d(28.457747632781057, 77.49689444467059);
    private Vector2d destination = new Vector2d(28.45695439935732, 77.49667533822735);
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
        StartCoroutine(InitializeLocationServices());
    }

    void Update()
    {
        UpdateDebugDisplay();
        UpdateUserPosition();

        if (locationInitialized && planeLocked && !routeRequested && !routeReceived && !arrowsSpawned)
        {
            //if (Coroutine.GetInstance().IsRunning(RequestNavigationRoute)) return;  // Skip if already running
            LogAR("🔍 Update detected ready state - triggering route generation");
            StartCoroutine(RequestNavigationRoute());
        }

        if (arrowsSpawned)
            UpdateArrowVisibility();
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

        yield return StartCoroutine(RequestNavigationRoute());
    }

    #endregion

    #region Route Management

    private IEnumerator RequestNavigationRoute()
    {
        LogAR("=== ENHANCED ROUTE REQUEST START ===");
        // FIX: Add a master reset here to prevent any old data or objects from persisting.
        ClearExistingArrows();
        rawRoutePoints.Clear();
        routeARPositions.Clear();
        selectedRouteIndices.Clear();
        routeReceived = false; // Also reset this flag
        arrowsSpawned = false;

        if (routeRequested)
        {
            LogAR("⚠️ Route already requested, forcing reset...");
            routeRequested = false;
        }

        routeRequested = true;
        LogAR($"🚀 Route request initiated - useTestRoute: {useTestRoute}");
        LogAR($"📍 From: {currentPosition.x:F6}, {currentPosition.y:F6}");
        LogAR($"📍 To: {destination.x:F6}, {destination.y:F6}");

        UpdateStatus("Getting route...");
        bool success = false;

        if (useTestRoute)
        {
            // Handle test route (no API call needed)
            try
            {
                LogAR("🧪 ENHANCED: Generating test route...");
                rawRoutePoints = GenerateTestRoute(currentPosition, destination, 20);

                if (rawRoutePoints != null && rawRoutePoints.Count > 0)
                {
                    routeReceived = true;
                    LogAR($"✅ Route generated: {rawRoutePoints.Count} points");
                    success = true;
                }
                else
                {
                    LogErrorAR("❌ Test route generation returned null or empty");
                    UpdateStatus("Route generation failed");
                    routeRequested = false;
                }
            }
            catch (System.Exception e)
            {
                LogErrorAR($"❌ Exception in test route generation: {e.Message}");
                routeRequested = false;
            }
        }
        else
        {
            // Handle real API call with coroutine
            yield return StartCoroutine(RequestRealRoute());
            success = routeReceived;
        }

        if (success)
        {
            LogAR("🎯 Starting ProcessRouteData...");
            yield return StartCoroutine(ProcessRouteData());
            LogAR("✅ ProcessRouteData completed");
        }

        LogAR("=== ROUTE REQUEST COMPLETED ===");
    }

    private IEnumerator RequestRealRoute()
    {
        LogAR("🌐 Querying real Mapbox Directions API...");

        bool queryCompleted = false;
        DirectionsResponse apiResponse = null;
        Vector2d[] waypoints = new Vector2d[] { currentPosition, destination };
        DirectionResource dr = new DirectionResource(waypoints, RoutingProfile.Walking);
        dr.Steps = true;

        _directions.Query(dr, (DirectionsResponse res) =>
        {
            apiResponse = res;
            queryCompleted = true;
        });

        // Wait for the query to complete
        float timeout = 10f;
        float elapsed = 0f;

        while (!queryCompleted && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (queryCompleted && apiResponse != null && apiResponse.Routes != null && apiResponse.Routes.Count > 0)
        {
            rawRoutePoints = apiResponse.Routes[0].Geometry;
            routeReceived = true;
            LogAR($"✅ Real route fetched: {rawRoutePoints.Count} points");
        }
        else
        {
            LogErrorAR("❌ Real route query failed or timed out - falling back to test route");
            rawRoutePoints = GenerateTestRoute(currentPosition, destination, 15);
            routeReceived = (rawRoutePoints != null && rawRoutePoints.Count > 0);
        }
    }

    // FIXED: ProcessRouteData method
    private IEnumerator ProcessRouteData()
    {
        LogAR("=== ENHANCED ROUTE PROCESSING START ===");
        LogAR($"📊 Input: {rawRoutePoints.Count} raw GPS points");

        if (rawRoutePoints == null || rawRoutePoints.Count == 0)
        {
            LogErrorAR("❌ No route points to process");
            yield break;
        }

        routeOrigin = rawRoutePoints[0];
        LogAR($"📍 Route origin set: {routeOrigin.x:F6}, {routeOrigin.y:F6}");

        routeARPositions.Clear();
        selectedRouteIndices.Clear();
        ClearExistingArrows();
        routeARPositions.Clear();  // Ensure no extras
        LogAR("🔄 Converting GPS points to AR coordinates...");

        for (int i = 0; i < rawRoutePoints.Count; i++)
        {
            Vector2d gpsPoint = rawRoutePoints[i];
            Vector3 arPosition = ConvertGPSToAR(gpsPoint);
            routeARPositions.Add(arPosition);

            if (i % 3 == 2) yield return null;
        }

        LogAR($"✅ Converted {routeARPositions.Count} points to AR coordinates");
        // FIX: For indoor testing, center the entire route in front of the camera for easy viewing.
        if (useTestRoute && routeARPositions.Count > 0 && arCamera != null)
        {
            // 1. Calculate the geometric center (centroid) of the route.
            Vector3 centroid = Vector3.zero;
            foreach (var pos in routeARPositions)
            {
                centroid += pos;
            }
            centroid /= routeARPositions.Count;
            centroid.y = 0f;

            // 2. CORRECTION: The offset is a fixed world distance (3m), so we remove the scale factor.
            Vector3 cameraForwardOffset = arCamera.transform.forward * 3f;
            cameraForwardOffset.y = 0f;

            // 3. Get the camera's horizontal rotation.
            Quaternion cameraYawRot = Quaternion.Euler(0f, arCamera.transform.eulerAngles.y, 0f);

            // 4. Reposition and rotate every point in the route.
            for (int i = 0; i < routeARPositions.Count; i++)
            {
                // Center the route at the world origin
                routeARPositions[i] -= centroid;
                // Rotate the route to face the same direction as the camera
                routeARPositions[i] = cameraYawRot * routeARPositions[i];
                // Move the now-centered-and-rotated route in front of the camera
                routeARPositions[i] += cameraForwardOffset;
            }
            LogAR($"📍 Route centered and rotated to appear in front of the camera.");
        }
        // ===== END OF NEW BLOCK =====


        // FIXED: Use proper spacing for indoor testing
        float actualSpacing = arrowSpacing; // Use the base spacing directly

        if (useTestRoute)
        {
            // For indoor testing, we want arrows closer together for better visualization
            actualSpacing = arrowSpacing; // Keep the 3m spacing you set
            LogAR($"🏠 Indoor mode: Using {actualSpacing}m spacing for better visualization");
        }
        else
        {
            actualSpacing = arrowSpacing * 2; // Outdoor can be more spaced
            LogAR($"🌍 Outdoor mode: Using {actualSpacing}m spacing");
        }

        LogAR($"🎯 Using selection spacing: {actualSpacing}m");

        List<int> selected = SelectRoutePoints(routeARPositions, actualSpacing);
        selectedRouteIndices = selected;

        LogAR($"📌 Selected {selectedRouteIndices.Count} points for arrows");
        UpdateStatus($"Route ready: {selectedRouteIndices.Count} arrows");

        LogAR("🚀 TRIGGERING ARROW SPAWNING...");
        yield return StartCoroutine(SpawnNavigationArrowsEnhanced());

        LogAR("=== ROUTE PROCESSING COMPLETED ===");
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
    StartCoroutine(RequestNavigationRoute());
    
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

    // ===== THIS IS THE CORRECTED AND FINAL VERSION =====
    private IEnumerator SpawnNavigationArrowsEnhanced()
    {
        LogAR("=== ENHANCED ARROW SPAWNING START ===");

        if (arrowsSpawned)
        {
            LogAR("⚠️ Arrows already spawned, clearing first...");
            ClearExistingArrows();
        }

        if (primaryPlane == null)
        {
            LogErrorAR("❌ Primary plane is NULL - cannot spawn arrows");
            yield break;
        }

        if (arrowPrefab == null)
        {
            LogErrorAR("❌ Arrow prefab is NULL - check assignment in inspector");
            yield break;
        }

        if (selectedRouteIndices.Count == 0)
        {
            LogErrorAR("❌ No selected route points for arrow placement");
            yield break;
        }

        LogAR($"🏗️ Spawning {selectedRouteIndices.Count} arrows...");
        int spawnedCount = 0;

        for (int i = 0; i < selectedRouteIndices.Count; i++)
        {
            bool success = false;
            string errorMessage = "";

            int routeIndex = selectedRouteIndices[i];
            Vector3 arPosition = routeARPositions[routeIndex];

            LogAR($"🎯 Spawning arrow {i + 1}/{selectedRouteIndices.Count}");

            Vector3 worldPosition = primaryPlane.transform.TransformPoint(arPosition);
            worldPosition.y = primaryPlane.transform.position.y + arrowHeightOffset;

            GameObject anchorObject = null;
            ARAnchor anchor = null;

            try
            {
                anchorObject = new GameObject($"NavArrowAnchor_{i + 1}");
                anchorObject.transform.position = worldPosition;
                anchorObject.transform.rotation = Quaternion.identity;
                anchor = anchorObject.AddComponent<ARAnchor>();

                if (anchor != null)
                {
                    success = true;
                }
                else
                {
                    errorMessage = "Failed to create ARAnchor component";
                }
            }
            catch (System.Exception e)
            {
                errorMessage = $"Exception creating anchor: {e.Message}";
            }

            if (!success)
            {
                LogErrorAR($"❌ Failed to create anchor for arrow {i + 1}: {errorMessage}");
                if (anchorObject != null)
                {
                    // FIX 2: Replaced DestroyImmediate with Destroy for safer runtime behavior.
                    Destroy(anchorObject);
                }
                continue;
            }

            GameObject arrow = null;
            try
            {
                arrow = Instantiate(arrowPrefab, anchor.transform);
                arrow.name = $"NavArrow_{i + 1}";
                arrow.transform.localPosition = Vector3.zero;
                arrow.transform.localScale = Vector3.one * arrowScale;

                // Calculate direction
                Vector3 direction = arCamera.transform.forward;  // Fallback to camera forward if too close/compass fails
                if (i < selectedRouteIndices.Count - 1)
                {
                    int nextIndex = selectedRouteIndices[i + 1];
                    Vector3 nextPosition = routeARPositions[nextIndex];
                    direction = nextPosition - arPosition;
                    float mag = direction.magnitude;
                    if (mag > 0.01f)
                    {
                        direction.Normalize();
                    }
                    else
                    {
                        LogAR($"⚠️ Arrow {i + 1}: Too close to next ({mag:F3}m), using camera forward");
                    }
                    direction.y = 0f;
                }

                // FIX 1: The rotation logic is updated to correctly combine North alignment, path direction, and user offset.
                // FIX 1: The rotation logic is updated to correctly combine North alignment, path direction, and user offset.
                if (direction.sqrMagnitude > 1e-6f)
                {
                    // --- START: FINAL ROTATION LOGIC ---
                    // 1. Calculate the local rotation that points the arrow along the path segment.
                    Quaternion localRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

                    // 2. Combine all rotations for the initial calculation.
                    Quaternion combinedRotation = arOriginWorldRot * northAlignment * localRotation * Quaternion.Euler(0f, arrowYawOffsetDegrees, 0f);

                    // 3. Extract the "forward" direction from that complex rotation.
                    Vector3 finalForward = combinedRotation * Vector3.forward;

                    // 4. CRITICAL FIX: Flatten this final direction vector onto the horizontal plane.
                    finalForward.y = 0;

                    // 5. Create a new, clean rotation from the flattened direction. This removes all unwanted tilt.
                    arrow.transform.rotation = Quaternion.LookRotation(finalForward.normalized, Vector3.up);

                    LogAR($"Arrow {i + 1} rotation: {arrow.transform.eulerAngles} (Dir: {direction})");
                    // --- END: FINAL ROTATION LOGIC ---
                }

                Color arrowColor = Color.yellow;
                if (i == 0)
                    arrowColor = Color.green;
                else if (i == selectedRouteIndices.Count - 1)
                    arrowColor = Color.blue;

                Renderer[] renderers = arrow.GetComponentsInChildren<Renderer>();
                foreach (var renderer in renderers)
                {
                    renderer.enabled = true;
                    renderer.material.color = arrowColor;
                }

                arrow.SetActive(true);

                spawnedArrows.Add(arrow);
                arrowAnchors.Add(anchor);

                activeArrows.Add(arrow);

                spawnedCount++;
                LogAR($"✅ Arrow {i + 1} spawned successfully");
            }
            catch (System.Exception e)
            {
                LogErrorAR($"❌ Exception spawning arrow {i + 1}: {e.Message}");
                if (arrow != null) Destroy(arrow);
                if (anchor.gameObject != null) Destroy(anchor.gameObject);
            }

            yield return new WaitForSeconds(0.1f);
        }
        if (spawnedCount != selectedRouteIndices.Count)
        {
            LogErrorAR($"⚠️ Spawn mismatch: Expected {selectedRouteIndices.Count}, Spawned {spawnedCount} - Check for duplicates!");
        }
        arrowsSpawned = true;
        LogAR($"🎉 ARROW SPAWNING COMPLETED: {spawnedCount}/{selectedRouteIndices.Count} arrows created");
        UpdateStatus($"Navigation ready: {spawnedCount} arrows");
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

    #endregion

    #region Utility Methods

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

    private void UpdateArrowVisibility()
    {
        if (!showAllArrows)
        {
            foreach (var arrowData in staticArrows)
            {
                if (arrowData.arrowObject != null)
                {
                    float distance = Vector3.Distance(userARPosition, arrowData.targetARPosition);
                    bool shouldBeVisible = distance <= maxVisibleDistance;
                    arrowData.arrowObject.SetActive(shouldBeVisible);
                }
            }
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

    private void UpdateDebugDisplay()
    {
        if (debugText != null)
        {
            debugText.text = $"=== AR NAV DEBUG V5.0 FIXED ===\n" +
                             $"Status: {currentStatus}\n" +
                             $"GPS: {(locationInitialized ? "Ready" : "Init...")}\n" +
                             $"Planes: {planesDetected} (Locked: {planeLocked})\n" +
                             $"Route Points: {rawRoutePoints.Count}\n" +
                             $"AR Positions: {routeARPositions.Count}\n" +
                             $"Selected: {selectedRouteIndices.Count}\n" +
                             $"Active Arrows: {activeArrows.Count}\n" +
                             $"Spawned: {spawnedArrows.Count}\n" +
                             $"Mode: {(useTestRoute ? "TEST/INDOOR" : "OUTDOOR/REAL")}\n" +
                             $"Scale Factor: {indoorScaleFactor}\n" +
                             $"Spacing: {arrowSpacing}m\n" +
                             $"GPS: {currentPosition.x:F6}, {currentPosition.y:F6}";
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
        StartCoroutine(RequestNavigationRoute());
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

    #endregion
}