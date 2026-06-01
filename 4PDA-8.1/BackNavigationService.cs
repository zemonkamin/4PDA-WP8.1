using System;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace _4PDA
{
    public sealed class BackNavigationEventArgs : EventArgs
    {
        public bool Handled { get; set; }
    }

    public static class BackNavigationService
    {
        private static Frame _rootFrame;
        private static bool _initialized;

        public static event EventHandler<BackNavigationEventArgs> BackRequested;

        public static void Initialize(Frame rootFrame)
        {
            _rootFrame = rootFrame;

            if (_initialized || Window.Current == null || Window.Current.CoreWindow == null)
                return;

            Window.Current.CoreWindow.Dispatcher.AcceleratorKeyActivated += Dispatcher_AcceleratorKeyActivated;
            _initialized = true;
        }

        public static bool GoBack(Frame frame)
        {
            if (frame != null && frame.CanGoBack)
            {
                frame.GoBack();
                return true;
            }

            return false;
        }

        private static void Dispatcher_AcceleratorKeyActivated(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args == null || args.EventType != CoreAcceleratorKeyEventType.KeyDown)
                return;

            if (!IsBackGesture(args))
                return;

            BackNavigationEventArgs backArgs = new BackNavigationEventArgs();
            EventHandler<BackNavigationEventArgs> handler = BackRequested;

            if (handler != null)
                handler(null, backArgs);

            if (!backArgs.Handled)
                backArgs.Handled = GoBack(_rootFrame);

            if (backArgs.Handled)
                args.Handled = true;
        }

        private static bool IsBackGesture(AcceleratorKeyEventArgs args)
        {
            VirtualKey key = args.VirtualKey;

            if (key == VirtualKey.GoBack)
                return true;

            if (key == VirtualKey.Escape)
                return true;

            if (key == VirtualKey.Left)
            {
                CoreVirtualKeyStates state = Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu);
                return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            }

            return false;
        }
    }
}
