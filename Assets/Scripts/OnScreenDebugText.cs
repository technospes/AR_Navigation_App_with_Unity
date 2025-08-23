using UnityEngine;
using TMPro;
using System.Collections.Generic;

public class OnscreenArrowLogger : MonoBehaviour
{
    // --- Drag these in the Inspector ---
    public TextMeshProUGUI logText;
    public GameObject logPanel; // The parent panel for the text and button

    private List<string> arrowLogs = new List<string>();
    private int currentIndex = 0;

    void Start()
    {
        // Start with the logger hidden
        if (logPanel != null)
        {
            logPanel.SetActive(false);
        }
    }

    // Call this from your arrow creation code
    public void LogArrowData(int index, int total, Vector3 worldPosition)
    {
        string logMessage = $"Arrow {index + 1}/{total}\nPos: {worldPosition.ToString("F1")}";
        arrowLogs.Add(logMessage);

        // Show the panel and the first log message as soon as it comes in
        if (!logPanel.activeSelf)
        {
            logPanel.SetActive(true);
            UpdateLogDisplay();
        }
    }

    // Hook this method to your UI Button's OnClick() event
    public void CycleToNextLog()
    {
        if (arrowLogs.Count == 0) return;

        currentIndex = (currentIndex + 1) % arrowLogs.Count;
        UpdateLogDisplay();
    }

    private void UpdateLogDisplay()
    {
        if (logText != null && arrowLogs.Count > 0)
        {
            logText.text = arrowLogs[currentIndex];
        }
    }

    public void ClearLogs()
    {
        arrowLogs.Clear();
        currentIndex = 0;
        if (logPanel != null)
        {
            logPanel.SetActive(false);
        }
    }
}