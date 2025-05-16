using UnityEngine;
using System.Collections.Generic;

public class LogManager : MonoBehaviour
{
    public static LogManager Instance { get; private set; }

    // Dictionary mapping a source name to its log messages
    private Dictionary<string, List<string>> logMessages = new Dictionary<string, List<string>>();

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // Log a message to a specific source
    public void Log(string source, string message)
    {
        if (!logMessages.ContainsKey(source))
        {
            logMessages[source] = new List<string>();
        }
        logMessages[source].Add(message);
        Debug.Log($"[{source}] {message}");
    }

    // Get log messages for a specific source
    public List<string> GetLogMessages(string source)
    {
        if (logMessages.ContainsKey(source))
        {
            return logMessages[source];
        }
        return new List<string>();
    }
}
