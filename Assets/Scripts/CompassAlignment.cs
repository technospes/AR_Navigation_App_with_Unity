//using System.Collections;
//using UnityEngine;

//public class CompassAlignment : MonoBehaviour
//{
//    [Header("Compass Settings")]
//    public Transform arRouteParent;
//    public bool enableCompassAlignment = true;
//    public bool debugCompass = true;
//    [SerializeField]
//    private bool invertHeading = true;

//    [Header("Compass Calibration")]
//    public float compassOffset = 0f; // Manual offset if needed
//    public bool useManualNorthDirection = false;
//    public Vector3 manualNorthDirection = Vector3.forward;

//    private RouteManager routeManager;

//    void Start()
//    {
//        if (!enableCompassAlignment)
//        {
//            Debug.Log("[Compass] Compass alignment disabled");
//            return;
//        }

//        if (arRouteParent == null)
//        {
//            Debug.LogError("[Compass] AR Route Parent is not assigned!");

//            // Try to find it automatically
//            GameObject parent = GameObject.Find("[AR_ROUTE_PARENT]");
//            if (parent != null)
//            {
//                arRouteParent = parent.transform;
//                Debug.Log("[Compass] Found AR Route Parent automatically");
//            }
//            else
//            {
//                Debug.LogError("[Compass] Could not find AR Route Parent!");
//                return;
//            }
//        }

//        // Get reference to RouteManager
//        routeManager = FindObjectOfType<RouteManager>();

//        StartCoroutine(AlignToCompass());
//    }

//    private IEnumerator AlignToCompass()
//    {
//        if (useManualNorthDirection)
//        {
//            Debug.Log("[Compass] Using manual north direction");
//            ApplyRotation(manualNorthDirection);
//            yield break;
//        }

//        Debug.Log("[Compass] Starting compass alignment...");

//        // Enable the compass
//        Input.compass.enabled = true;

//        if (routeManager != null)
//        {
//            routeManager.UpdateStatus("Starting compass...");
//        }

//        // Wait for compass to initialize
//        float initialHeading = 0;
//        int waitCount = 0;
//        bool compassReady = false;

//        while (!compassReady && waitCount < 100) // Wait max 10 seconds
//        {
//            // Check if compass is providing valid data
//            float currentHeading = Input.compass.trueHeading;

//            if (debugCompass)
//            {
//                Debug.Log($"[Compass] Wait {waitCount}: Heading = {currentHeading}, Raw = {Input.compass.rawVector}");
//            }

//            // Consider compass ready if we get a non-zero heading or after some attempts
//            if (currentHeading > 0 || waitCount > 50)
//            {
//                initialHeading = currentHeading;
//                compassReady = true;
//                break;
//            }

//            yield return new WaitForSeconds(0.1f);
//            waitCount++;
//        }

//        if (!compassReady || initialHeading == 0)
//        {
//            Debug.LogWarning("[Compass] Compass not available or failed to initialize. Using fallback.");

//            if (routeManager != null)
//            {
//                routeManager.UpdateStatus("Compass failed - using default orientation");
//            }

//            // Use default orientation (no rotation)
//            yield break;
//        }

//        // Apply compass offset if specified
//        float adjustedHeading = (invertHeading ? -initialHeading : initialHeading) + compassOffset;

//        Debug.Log($"[Compass] Compass initialized successfully!");
//        Debug.Log($"[Compass] True North heading: {initialHeading:F1}°");
//        Debug.Log($"[Compass] Adjusted heading (with offset): {adjustedHeading:F1}°");

//        if (routeManager != null)
//        {
//            routeManager.UpdateStatus($"Compass aligned: {adjustedHeading:F1}°");
//        }

//        // Apply the rotation to align with True North
//        // The rotation aligns the AR world's Z-axis (forward) with True North
//        Vector3 northDirection = Quaternion.Euler(0, adjustedHeading, 0) * Vector3.forward;
//        ApplyRotation(northDirection);
//    }

//    private void ApplyRotation(Vector3 northDirection)
//    {
//        if (arRouteParent == null) return;

//        // Calculate rotation to align with north direction
//        Quaternion targetRotation = Quaternion.LookRotation(northDirection, Vector3.up);

//        // Apply rotation to the route parent
//        arRouteParent.rotation = targetRotation;

//        Debug.Log($"[Compass] Applied rotation: {targetRotation.eulerAngles}");
//        Debug.Log($"[Compass] AR Route Parent now aligned with True North");

//        if (debugCompass)
//        {
//            Debug.Log($"[Compass] North Direction Vector: {northDirection}");
//            Debug.Log($"[Compass] Final Rotation: {arRouteParent.rotation.eulerAngles}");
//        }
//    }

//    // Public method to recalibrate compass
//    public void RecalibrateCompass()
//    {
//        Debug.Log("[Compass] Manual recalibration requested");

//        if (enableCompassAlignment)
//        {
//            StartCoroutine(AlignToCompass());
//        }
//    }

//    // Public method to set manual offset
//    public void SetCompassOffset(float offset)
//    {
//        compassOffset = offset;
//        Debug.Log($"[Compass] Compass offset set to: {offset}°");

//        if (enableCompassAlignment && !useManualNorthDirection)
//        {
//            StartCoroutine(AlignToCompass());
//        }
//    }

//    void OnDestroy()
//    {
//        // Disable compass when not needed
//        if (Input.compass.enabled)
//        {
//            Input.compass.enabled = false;
//        }
//    }
//}