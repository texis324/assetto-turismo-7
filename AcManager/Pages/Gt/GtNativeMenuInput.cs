using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AcManager.Pages.Windows;
using FirstFloor.ModernUI.Helpers;
using FirstFloor.ModernUI.Windows.Controls;

namespace AcManager.Pages.Gt {
    /// <summary>Basic controller navigation for the native CM pages opened by CM7's map.</summary>
    internal static class GtNativeMenuInput {
        private static readonly HashSet<string> Pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "/Pages/Drive/SpecialEvents.xaml",
            "/Pages/Miscellaneous/PlayerStats.xaml",
            "/Pages/Drive/KunosCareer.xaml",
            "/Pages/Drive/KunosCareer_SelectedPage.xaml",
            "/Pages/Drive/Online.xaml",
            "/Pages/Lists/ReplaysListPage.xaml",
            "/Pages/Lists/TracksListPage.xaml"
        };

        internal static bool IsSupportedPage(string uri) {
            return uri != null && Pages.Contains(PagePath(uri));
        }

        private static string PagePath(string uri) {
            var query = uri.IndexOfAny(new[] { '?', '#' });
            return query < 0 ? uri : uri.Substring(0, query);
        }

        private static FrameworkElement CurrentPage(MainWindow main) {
            if (main == null || !main.IsActive || !main.IsVisible || !main.IsEnabled
                    || !GtIdleSlides.IsOurForeground(null)) return null;
            var uri = main.CurrentSource?.OriginalString;
            if (!IsSupportedPage(uri)) return null;
            foreach (Window window in Application.Current.Windows) {
                if (!ReferenceEquals(window, main) && window.IsVisible && window.IsActive) return null;
            }
            var frame = main.Template?.FindName("ContentFrame", main) as ModernFrame;
            var page = frame?.Content as FrameworkElement;
            var expectedType = "AcManager." + PagePath(uri).TrimStart('/').Replace('/', '.');
            expectedType = expectedType.Substring(0, expectedType.Length - ".xaml".Length);
            // Source updates before async navigation finishes: never operate the previous page.
            return page != null && page.IsLoaded && page.IsVisible && page.IsEnabled
                    && string.Equals(page.GetType().FullName, expectedType, StringComparison.OrdinalIgnoreCase)
                    && PresentationSource.FromVisual(page) != null ? page : null;
        }

        internal static bool Handle(GtPadCommand command, Action<Window, Key> sendKey) {
            var main = Application.Current?.MainWindow as MainWindow;
            if (!IsSupportedPage(main?.CurrentSource?.OriginalString)) return false;
            try {
                var page = CurrentPage(main);
                if (page == null) return true;
                // This branch is dispatched before GtFocus. A cached GT page must never receive these commands.
                GtFocus.OnRealMouseMove();
                if (command == GtPadCommand.Back) {
                    if (GtNav.GoBack()) GtSfx.PlayCancel();
                    return true;
                }
                var focused = Keyboard.FocusedElement as DependencyObject;
                if (command == GtPadCommand.L1 || command == GtPadCommand.R1 || command == GtPadCommand.Select) {
                    MoveFocus(page, focused, command == GtPadCommand.L1);
                    return true;
                }
                var direction = command >= GtPadCommand.Up && command <= GtPadCommand.Right;
                if (!direction && command != GtPadCommand.Confirm) return true;
                if (!IsWithin(page, focused)) {
                    // First press only establishes visible WPF focus, matching the GT page behavior.
                    MoveFocus(page, null, false);
                    return true;
                }
                if (IsProtectedInput(focused)) {
                    // Never type into search/password/editable fields or invoke their default commands.
                    if (direction) MoveFocus(page, focused, command == GtPadCommand.Up || command == GtPadCommand.Left);
                    return true;
                }
                if (command == GtPadCommand.Confirm) {
                    // Space down+up uses native ButtonBase behavior, including commands and toggle state.
                    PressKey(main, page, focused, focused is ButtonBase ? Key.Space : Key.Enter, sendKey);
                    return true;
                }
                var key = command == GtPadCommand.Up ? Key.Up : command == GtPadCommand.Down ? Key.Down
                        : command == GtPadCommand.Left ? Key.Left : Key.Right;
                if (FindParent<Selector>(focused, page) != null || FindParent<TreeView>(focused, page) != null
                        || FindParent<Slider>(focused, page) != null) {
                    PressKey(main, page, focused, key, sendKey);
                } else {
                    // WPF spatial focus handles ordinary buttons; Tab order is the fallback at an edge.
                    var element = focused as UIElement;
                    var move = command == GtPadCommand.Up ? FocusNavigationDirection.Up : command == GtPadCommand.Down ? FocusNavigationDirection.Down
                            : command == GtPadCommand.Left ? FocusNavigationDirection.Left : FocusNavigationDirection.Right;
                    if (element == null || !element.MoveFocus(new TraversalRequest(move))) {
                        MoveFocus(page, focused, command == GtPadCommand.Up || command == GtPadCommand.Left);
                    } else if (!IsWithin(page, Keyboard.FocusedElement as DependencyObject)) {
                        MoveFocus(page, null, command == GtPadCommand.Up || command == GtPadCommand.Left);
                    }
                }
                return true;
            } catch (Exception ex) {
                Logging.Warning("[GtNativeMenuInput] " + ex.Message);
                return true;
            }
        }

        internal static bool IsProtectedInput(DependencyObject focused) {
            for (var current = focused; current != null; current = Parent(current)) {
                if (current is TextBoxBase || current is PasswordBox || current is ComboBox) return true;
            }
            return false;
        }

        private static void PressKey(MainWindow main, FrameworkElement page, DependencyObject focused, Key key, Action<Window, Key> sendKey) {
            if (sendKey == null || CurrentPage(main) != page || IsProtectedInput(focused)) return;
            sendKey(main, key);
            // A key-down action may navigate or open a dialog. Do not release the key into that new surface.
            if (CurrentPage(main) != page || !ReferenceEquals(Keyboard.FocusedElement, focused)) return;
            var source = PresentationSource.FromVisual(main);
            if (source != null) InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                RoutedEvent = Keyboard.PreviewKeyUpEvent
            });
        }

        private static void MoveFocus(FrameworkElement page, DependencyObject focused, bool previous) {
            var element = IsWithin(page, focused) ? focused as UIElement : null;
            var moved = element != null && element.MoveFocus(new TraversalRequest(previous ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            if (!moved || !IsWithin(page, Keyboard.FocusedElement as DependencyObject)) {
                page.MoveFocus(new TraversalRequest(previous ? FocusNavigationDirection.Last : FocusNavigationDirection.First));
            }
            (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
            GtSfx.PlayMove();
        }

        private static T FindParent<T>(DependencyObject value, DependencyObject boundary) where T : DependencyObject {
            for (var current = value; current != null; current = Parent(current)) {
                var match = current as T;
                if (match != null) return match;
                if (ReferenceEquals(current, boundary)) break;
            }
            return null;
        }

        private static bool IsWithin(DependencyObject parent, DependencyObject value) {
            for (var current = value; current != null; current = Parent(current)) {
                if (ReferenceEquals(current, parent)) return true;
            }
            return false;
        }

        private static DependencyObject Parent(DependencyObject value) {
            var content = value as FrameworkContentElement;
            if (content != null) return content.Parent;
            return value is Visual ? VisualTreeHelper.GetParent(value) : LogicalTreeHelper.GetParent(value);
        }
    }
}
