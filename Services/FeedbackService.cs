using System;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TolkIsland.Services;

/// <summary>
/// Optional notification chime and Precision Touchpad haptic. Both stay off
/// unless the user opts in — an always-on overlay should not make noise by default.
/// </summary>
public static class FeedbackService
{
    [DllImport("user32.dll")]
    private static extern int MessageBeep(uint type);

    public static void Notify()
    {
        var settings = SettingsService.Current;
        if (settings.NotificationSounds)
        {
            try
            {
                SystemSounds.Asterisk.Play();
            }
            catch
            {
                try
                {
                    MessageBeep(0x00000040);
                }
                catch
                {
                    // No audio device.
                }
            }
        }

        if (settings.HapticsEnabled)
        {
            _ = TryHapticAsync();
        }
    }

    private static async Task TryHapticAsync()
    {
        try
        {
            var device = await Windows.Devices.Haptics.VibrationDevice.GetDefaultAsync();
            var feedback = device?.SimpleHapticsController.SupportedFeedback;
            if (device is null || feedback is null || feedback.Count == 0)
            {
                return;
            }

            // SendHapticFeedbackForDuration is synchronous; run it off the UI
            // thread so a slow HID round-trip cannot stall a notification card.
            await Task.Run(() =>
                device.SimpleHapticsController.SendHapticFeedbackForDuration(
                    feedback[0],
                    0.4,
                    TimeSpan.FromMilliseconds(40)));
        }
        catch
        {
            // Most PCs have no haptic device.
        }
    }
}
