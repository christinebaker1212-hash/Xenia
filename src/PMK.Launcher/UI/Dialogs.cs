using System;
using System.Runtime.InteropServices;
using SDL3;

namespace PMK.Launcher
{
    /// <summary>Native message boxes and file pickers through SDL3.</summary>
    public static class Dialogs
    {
        public static IntPtr Window { get; set; }

        public static void Error(string title, string message) => Show(SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR, title, message);
        public static void Info(string title, string message) => Show(SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_INFORMATION, title, message);

        private static void Show(SDL.SDL_MessageBoxFlags flags, string title, string message)
        {
            Log.Write($"{title}: {message}");
            if (Program.Options?.AutoFrames > 0)
                return; // Never block automated runs.
            try
            {
                SDL.SDL_ShowSimpleMessageBox(flags, title, message, Window);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                // No SDL yet: the log line is all we can do.
            }
        }

        private static SDL.SDL_DialogFileCallback keepAlive;

        /// <summary>Asynchronous file picker; <paramref name="done"/> receives the path or null.</summary>
        public static void PickFile(string title, Action<string> done)
        {
            keepAlive = (userdata, filelist, filter) =>
            {
                string path = null;
                if (filelist != IntPtr.Zero)
                {
                    IntPtr first = Marshal.ReadIntPtr(filelist);
                    if (first != IntPtr.Zero) path = Marshal.PtrToStringUTF8(first);
                }
                done(path);
            };
            try
            {
                SDL.SDL_ShowOpenFileDialog(keepAlive, IntPtr.Zero, Window, null, 0, null, false);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                done(null);
            }
        }
    }
}
