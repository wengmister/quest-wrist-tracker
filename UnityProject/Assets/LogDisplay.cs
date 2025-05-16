using UnityEngine;
using UnityEngine.UI;

public class LogDisplay : MonoBehaviour
{
    [SerializeField] private Text logText;
    [SerializeField] private int maxDisplayedMessages = 20;
    // New: the source of logs this display should show
    [SerializeField] private string logSource;

    private void Update()
    {
        DisplayLog();
    }

    private void DisplayLog()
    {
        var logMessages = LogManager.Instance.GetLogMessages(logSource);
        int startIdx = Mathf.Max(0, logMessages.Count - maxDisplayedMessages);
        string displayedLog = "";
        for (int i = startIdx; i < logMessages.Count; i++)
        {
            displayedLog += logMessages[i] + "\n";
        }
        logText.text = displayedLog;
    }
}
