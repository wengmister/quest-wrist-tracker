using UnityEngine;
using Oculus.Interaction.Input;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System;
using System.Collections.Generic;

public enum HandSide { Left, Right }

public class HandJointAnglesLogger : MonoBehaviour
{
    [SerializeField]
    private bool _logToHUD = true;

    [SerializeField]
    private float _logFrequency = 0.01f; // Log every 0.01 seconds

    // Specify which hand this logger is for.
    [SerializeField] private HandSide handSide;

    private IHand _hand;
    private float _timer = 0f;
    
    public string remoteIP = "255.255.255.255"; // Broadcast address
    public int remotePort = 9000; // Port to send data to
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    
    // Add reference to wrist joint index
    private const int WRIST_JOINT_INDEX = 0; // Typically the wrist is joint 0 in Oculus hand tracking

    private void Start()
    {
        _hand = GetComponent<IHand>();
        if (_hand == null)
        {
            LogHUD("HandJointAnglesLogger requires a component that implements IHand on the same GameObject");
            enabled = false;
            return;
        }

        // Subscribe to hand updates
        _hand.WhenHandUpdated += OnHandUpdated;

        udpClient = new UdpClient();
        remoteEndPoint = new IPEndPoint(IPAddress.Parse(remoteIP), remotePort);
    }

    private void OnDestroy()
    {
        if (_hand != null)
        {
            _hand.WhenHandUpdated -= OnHandUpdated;
        }
        
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }
    }

    private void OnHandUpdated()
    {
        // This will be called whenever the hand data is updated
        if (_logToHUD)
        {
            _timer += Time.deltaTime;
            if (_timer >= _logFrequency)
            {
                _timer = 0f;
                LogJointAngles();
                LogWristData(); // Add logging of wrist data
            }
        }
    }

    /// <summary>
    /// Logs the wrist position and orientation to the HUD and sends over UDP
    /// </summary>
    private void LogWristData()
    {
        if (!_hand.IsTrackedDataValid)
        {
            LogHUD("Wrist tracking not valid");
            return;
        }

        // Using the GetRootPose method since GetJointPosesFromRoot is not available
        if (_hand.GetRootPose(out Pose rootPose))
        {
            // Root pose should represent the wrist/palm center in world space
            Vector3 wristPosition = rootPose.position;
            Quaternion wristRotation = rootPose.rotation;
            
            // Convert quaternion to euler angles for HUD display only
            Vector3 wristEulerAngles = wristRotation.eulerAngles;
            
            // Build display message for HUD
            string displayMessage = $"{handSide} Wrist Data:\n";
            displayMessage += $"Position: ({wristPosition.x:F3}, {wristPosition.y:F3}, {wristPosition.z:F3})\n";
            displayMessage += $"Rotation (Euler): ({wristEulerAngles.x:F1}°, {wristEulerAngles.y:F1}°, {wristEulerAngles.z:F1}°)\n";
            displayMessage += $"Rotation (Quat): ({wristRotation.x:F3}, {wristRotation.y:F3}, {wristRotation.z:F3}, {wristRotation.w:F3})\n";
            
            // Log to HUD
            LogHUD(displayMessage);
            
            // Build CSV message for UDP with quaternion values
            string csvMessage = $"{handSide} wrist:";
            csvMessage += $", {wristPosition.x:F3}, {wristPosition.y:F3}, {wristPosition.z:F3}";
            csvMessage += $", {wristRotation.x:F3}, {wristRotation.y:F3}, {wristRotation.z:F3}, {wristRotation.w:F3}";
            
            // Send over UDP
            SendUdpMessage(csvMessage);
        }
        else
        {
            LogHUD("Failed to get wrist pose");
        }
    }

    /// <summary>
    /// Computes the joint angles, logs detailed output to the HUD,
    /// and broadcasts only the 16 comma separated numeric values (with hand type) over UDP.
    /// </summary>
    private void LogJointAngles()
    {
        if (!_hand.IsTrackedDataValid)
        {
            LogHUD("Hand tracking not valid");
            return;
        }

        if (_hand.GetJointPosesLocal(out ReadOnlyHandJointPoses jointPoses))
        {
            // Build the detailed display string for the HUD (using the original helper methods)
            string displayMessage = handSide.ToString() + " hand Angles:\n";
            displayMessage += GetThumbAngles(jointPoses);
            displayMessage += GetFingerAngles(jointPoses, HandFinger.Index);
            displayMessage += GetFingerAngles(jointPoses, HandFinger.Middle);
            displayMessage += GetFingerAngles(jointPoses, HandFinger.Ring);
            displayMessage += GetFingerAngles(jointPoses, HandFinger.Pinky);

            // Build the UDP CSV string containing only the 16 numeric values.
            List<float> udpAngles = new List<float>();

            // --- Thumb (4 angles) ---
            int[] thumbIndices = GetFingerJointIndices(HandFinger.Thumb);
            if (thumbIndices.Length < 4)
            {
                LogHUD("Insufficient joint data for Thumb");
                return;
            }
            // Transition: CMC → MCP
            Quaternion cmcToMcp = Quaternion.Inverse(jointPoses[thumbIndices[0]].rotation) * jointPoses[thumbIndices[1]].rotation;
            Vector3 cmcToMcpEuler = cmcToMcp.eulerAngles;
            float cmcMcpFlexion = NormalizeAngle(cmcToMcpEuler.x);
            float cmcMcpAdduction = NormalizeAngle(cmcToMcpEuler.y);

            // Transition: MCP → IP
            Quaternion mcpToIp = Quaternion.Inverse(jointPoses[thumbIndices[1]].rotation) * jointPoses[thumbIndices[2]].rotation;
            Vector3 mcpToIpEuler = mcpToIp.eulerAngles;
            float mcpIpFlexion = NormalizeAngle(mcpToIpEuler.x);
            float mcpIpAdduction = NormalizeAngle(mcpToIpEuler.y);

            udpAngles.Add(cmcMcpFlexion);
            udpAngles.Add(cmcMcpAdduction);
            udpAngles.Add(mcpIpFlexion);
            udpAngles.Add(mcpIpAdduction);

            // --- Non-thumb fingers (Index, Middle, Ring, Pinky; 3 angles each) ---
            HandFinger[] fingers = new HandFinger[] { HandFinger.Index, HandFinger.Middle, HandFinger.Ring, HandFinger.Pinky };
            foreach (HandFinger finger in fingers)
            {
                int[] indices = GetFingerJointIndices(finger);
                if (indices.Length < 5)
                {
                    LogHUD("Insufficient joint data for " + finger.ToString());
                    return;
                }
                // MCP → PIP rotation
                Quaternion mcpToPip = Quaternion.Inverse(jointPoses[indices[1]].rotation) * jointPoses[indices[2]].rotation;
                Vector3 mcpEuler = mcpToPip.eulerAngles;
                float mcpFlexion = NormalizeAngle(mcpEuler.x);
                float mcpAdduction = NormalizeAngle(mcpEuler.y);

                // PIP → DIP rotation (flexion only)
                Quaternion pipToDip = Quaternion.Inverse(jointPoses[indices[2]].rotation) * jointPoses[indices[3]].rotation;
                pipToDip.ToAngleAxis(out float pipAngle, out Vector3 pipAxis);
                float pipFlexion = NormalizeAngle(pipAngle);
                // Compensate by adding mcp flexion to pip flexion
                pipFlexion += mcpFlexion;

                udpAngles.Add(mcpFlexion);
                udpAngles.Add(mcpAdduction);
                udpAngles.Add(pipFlexion);
            }

            // Build the CSV string for UDP (hand type followed by 16 comma separated values)
            string csvMessage = $"{handSide} hand:";
            foreach (float angle in udpAngles)
            {
                csvMessage += $", {angle:F1}";
            }

            // Display the detailed message on the HUD and send the CSV string over UDP.
            LogHUD(displayMessage);
            SendUdpMessage(csvMessage);
        }
        else
        {
            LogHUD("Failed to get joint poses");
        }
    }

    /// <summary>
    /// Returns a multi-line string containing all thumb joint angles for display.
    /// Each line is formatted as "JointName: Angle".
    /// </summary>
    private string GetThumbAngles(ReadOnlyHandJointPoses jointPoses)
    {
        int[] indices = GetFingerJointIndices(HandFinger.Thumb);
        if (indices.Length < 4)
        {
            return "Insufficient joint data for Thumb\n";
        }

        // Transition: CMC → MCP
        Quaternion cmcToMcp = Quaternion.Inverse(jointPoses[indices[0]].rotation) * jointPoses[indices[1]].rotation;
        Vector3 cmcToMcpEuler = cmcToMcp.eulerAngles;
        float cmcMcpFlexion = NormalizeAngle(cmcToMcpEuler.x);
        float cmcMcpAdduction = NormalizeAngle(cmcToMcpEuler.y);

        // Transition: MCP → IP
        Quaternion mcpToIp = Quaternion.Inverse(jointPoses[indices[1]].rotation) * jointPoses[indices[2]].rotation;
        Vector3 mcpToIpEuler = mcpToIp.eulerAngles;
        float mcpIpFlexion = NormalizeAngle(mcpToIpEuler.x);
        float mcpIpAdduction = NormalizeAngle(mcpToIpEuler.y);

        string s = "";
        s += $"Thumb CMC Flexion: {cmcMcpFlexion:F1}°\n";
        s += $"Thumb CMC Adduction: {cmcMcpAdduction:F1}°\n";
        s += $"Thumb MCP Flexion: {mcpIpFlexion:F1}°\n";
        s += $"Thumb MCP Adduction: {mcpIpAdduction:F1}°\n";
        return s;
    }

    /// <summary>
    /// Returns a multi-line string containing joint angles for a non-thumb finger for display.
    /// For non-thumb fingers, we compute:
    /// - MCP→PIP rotation (using Euler angles for flexion and adduction)
    /// - PIP→DIP rotation (using axis–angle for flexion)
    /// Each line is formatted as "JointName: Angle".
    /// </summary>
    private string GetFingerAngles(ReadOnlyHandJointPoses jointPoses, HandFinger finger)
    {
        int[] indices = GetFingerJointIndices(finger);
        if (indices.Length < 5)
        {
            return $"Insufficient joint data for {finger} finger\n";
        }

        // Compute MCP → PIP rotation
        Quaternion mcpToPip = Quaternion.Inverse(jointPoses[indices[1]].rotation) * jointPoses[indices[2]].rotation;
        Vector3 mcpEuler = mcpToPip.eulerAngles;
        float mcpFlexion = NormalizeAngle(mcpEuler.x);
        float mcpAdduction = NormalizeAngle(mcpEuler.y);

        // Compute PIP → DIP rotation using axis–angle (flexion only)
        Quaternion pipToDip = Quaternion.Inverse(jointPoses[indices[2]].rotation) * jointPoses[indices[3]].rotation;
        pipToDip.ToAngleAxis(out float pipAngle, out Vector3 pipAxis);
        float pipFlexion = NormalizeAngle(pipAngle);
        // Compensate by adding mcp flexion to pip flexion
        pipFlexion += mcpFlexion;

        // Compute DIP -> TIP rotation using axis-angle (flexion only)
        // Quaternion dipToTip = Quaternion.Inverse(jointPoses[indices[3]].rotation) * jointPoses[indices[4]].rotation;
        // dipToTip.ToAngleAxis(out float tipAngle, out Vector3 tipAxis);  
        // float dipFlexion = NormalizeAngle(tipAngle);

        string s = "";
        s += $"{finger} MCP Flexion: {mcpFlexion:F1}°\n";
        s += $"{finger} MCP Adduction: {mcpAdduction:F1}°\n";
        s += $"{finger} PIP Flexion: {pipFlexion:F1}°\n";
        // s += $"{finger} DIP Flexion: {dipFlexion:F1}°\n";
        return s;
    }

    /// <summary>
    /// Normalizes an angle (in degrees) to the range [-180, 180].
    /// </summary>
    private float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
        {
            angle -= 360f;
        }
        return angle;
    }

    /// <summary>
    /// Returns joint indices for the given finger based on the standard Oculus hand skeleton.
    /// For the thumb, we assume a 4-joint chain.
    /// For non-thumb fingers, we assume a 5-joint chain.
    /// Adjust these if your actual data uses different indexing.
    /// </summary>
    private int[] GetFingerJointIndices(HandFinger finger)
    {
        switch (finger)
        {
            case HandFinger.Thumb:
                return new int[] { 1, 2, 3, 4 };
            case HandFinger.Index:
                return new int[] { 5, 6, 7, 8, 9 };
            case HandFinger.Middle:
                return new int[] { 10, 11, 12, 13, 14 };
            case HandFinger.Ring:
                return new int[] { 15, 16, 17, 18, 19 };
            case HandFinger.Pinky:
                return new int[] { 20, 21, 22, 23, 24 };
            default:
                return new int[0];
        }
    }

    /// <summary>
    /// Sends the provided message over UDP.
    /// </summary>
    void SendUdpMessage(string message)
    {
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            udpClient.Send(data, data.Length, remoteEndPoint);
        }
        catch (Exception ex)
        {
            LogHUD("UDP Send Error: " + ex.Message);
        }
    }

    // Helper method to log messages to the HUD on the channel for the current hand side.
    private void LogHUD(string message)
    {
        LogManager.Instance.Log(handSide.ToString(), message);
    }
}