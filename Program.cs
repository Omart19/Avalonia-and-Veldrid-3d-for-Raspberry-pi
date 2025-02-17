﻿using Avalonia;
using Avalonia.ReactiveUI;
using System;



namespace VeldridSTLViewer;

internal class Program
{
    [STAThread]
public static void Main(string[] args)
{
        
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}

// Avalonia configuration, don't remove; also used by visual designer.
public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace()
        .UseSkia(); // Use Skia for rendering

}
