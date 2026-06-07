using Oculus.Interaction;
using UnityEngine;



public class SwitchState : MonoBehaviour
{
    public bool isOn = false; // başlangıçta kapalı
    public float offThreshold = -115f; // -90 ile -140 arasının ortası

    private bool wasOn;
    private bool initialized = false;

    private void Update()
    {
        float currentAngle = transform.localEulerAngles.x;

        if (currentAngle > 180)
            currentAngle -= 360;

        Debug.Log($"Angle X: {currentAngle:F1}");

        // -140 açık, -90 kapalı olduğu için mantık tersine döndü
        isOn = currentAngle < offThreshold;

        if (!initialized)
        {
            wasOn = isOn;
            initialized = true;
        }

        if (!isOn && wasOn)
            Debug.Log($"{gameObject.name} - Switch KAPALI");
        else if (isOn && !wasOn)
            Debug.Log($"{gameObject.name} - Switch AÇIK");

        wasOn = isOn;
    }
}