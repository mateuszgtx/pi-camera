using pi_camera.Services;

namespace pi_camera;

public static partial class Program
{
    private static async Task HardwarePasswordResetLoopAsync(List<GpioShutterService> functionButtons)
    {
        if (functionButtons.Count < 2)
            return;

        DateTime allPressedSinceUtc = DateTime.MinValue;
        var resetAlreadyTriggered = false;

        while (_running)
        {
            var allPressed = functionButtons.All(button => button.IsPressed);
            var now = DateTime.UtcNow;

            if (!allPressed)
            {
                allPressedSinceUtc = DateTime.MinValue;
                resetAlreadyTriggered = false;
                await Task.Delay(200);
                continue;
            }

            if (allPressedSinceUtc == DateTime.MinValue)
                allPressedSinceUtc = now;

            if (!resetAlreadyTriggered && (now - allPressedSinceUtc).TotalSeconds >= 10)
            {
                ClearWebPassword("hardware buttons held for 10 seconds");
                resetAlreadyTriggered = true;
            }

            await Task.Delay(200);
        }
    }
}
