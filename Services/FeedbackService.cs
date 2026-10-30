using System;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TolkIsland.Services;

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

            await Task.Run(() =>
                device.SimpleHapticsController.SendHapticFeedbackForDuration(
                    feedback[0],
                    0.4,
                    TimeSpan.FromMilliseconds(40)));
        }
        catch
        {
        }
    }
}
