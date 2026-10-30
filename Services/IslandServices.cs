namespace TolkIsland.Services;

public sealed class IslandServices
{
    public static IslandServices Shared { get; } = new();

    public MediaSessionService Media { get; } = new();
    public NotificationListenerService Notifications { get; } = new();
    public FocusTimerService FocusTimer { get; } = new();
    public AudioVolumeService Audio { get; } = new();
    public AudioVisualizerService Visualizer { get; } = new();
    public BrightnessService Brightness { get; } = new();
    public ShelfService Shelf { get; } = new();
    public LocalApiService LocalApi { get; } = new();
    public MirrorService Mirror { get; } = new();
    public NotesService Notes { get; } = new();
    public CalendarService Calendar { get; } = new();
    public DownloadWatchService Downloads { get; } = new();

    public void Shutdown()
    {
        Media.Dispose();
        Mirror.Dispose();
        FocusTimer.Dispose();
        Notifications.Dispose();
        Audio.Dispose();
        Visualizer.Dispose();
        Brightness.Dispose();
        LocalApi.Dispose();
        Calendar.Dispose();
        Downloads.Dispose();
    }
}
