using UnityEngine;

/// <summary>
/// Common face of every physical controller the game can use. Game scripts
/// should reference THIS type (not JoyconInputProvider / ArduinoInputProvider),
/// so you can swap controllers by dragging a different provider component into
/// the same Inspector slot.
///
/// Members mirror what JoyconInputProvider already exposes:
///   Connected              controller found and talking to the game
///   ActionPressedThisFrame true for exactly one frame when the action button goes down
///   Steer                  -1..1  tilt left/right          (Eat)
///   Pitch                  -1..1  tilt down/up             (Eat)
///   Stillness              0..1   1 = held perfectly still (Return)
///   RockedThisFrame        true for one frame per side-to-side rock (Rise)
///   ResetRock()            call when Rise begins
///   CalibrateTilt()        call when Eat begins: current hold = "straight ahead"
/// </summary>
public abstract class ControllerInputProvider : MonoBehaviour
{
    public abstract bool Connected { get; }
    public abstract bool ActionPressedThisFrame { get; }
    public abstract bool RockedThisFrame { get; }
    public abstract float Steer { get; }
    public abstract float Pitch { get; }
    public abstract float Stillness { get; }

    public abstract void ResetRock();
    public abstract void CalibrateTilt();
}
