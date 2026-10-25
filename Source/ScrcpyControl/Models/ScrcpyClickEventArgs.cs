namespace ScrcpyControl.Models;

public sealed class ScrcpyClickEventArgs : EventArgs
{
    public ScrcpyClickEventArgs(int screenX, int screenY)
    {
        ScreenX = screenX;
        ScreenY = screenY;
    }

    public int ScreenX { get; }
    public int ScreenY { get; }
}
