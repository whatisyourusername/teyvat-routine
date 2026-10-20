namespace TeyvatRoutine;
public static class MouseRouting
{
    public static bool RelativeMode(bool automatic, bool? cursorVisible, bool manualRelative)
        => automatic && cursorVisible.HasValue ? !cursorVisible.Value : manualRelative;
    public static bool PauseForClick(bool relative, bool inBounds, bool belongsToGame)
        => !relative && (!inBounds || !belongsToGame);
}
