using System;
using System.Windows;

namespace TranslatorAnywhere.Models;

// Bounds and Pointer use physical screen pixels, including negative monitor coordinates.
public sealed record SelectionSnapshot(string Text, string ApplicationName, IntPtr SourceWindow,
    Rect Bounds, Point Pointer, string Method, DateTimeOffset CapturedAt);

public sealed record SelectionGesture(IntPtr SourceWindow, Point Start, Point End, bool IsDoubleClick);
