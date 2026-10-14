using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GifPin.UI;

/// <summary>
/// 숫자를 돌려서 맞추는 시계 (시 : 분, 24시간).
/// 각 숫자 바퀴는 마우스 휠, 위아래로 끌기, 위·아래 숫자 클릭, 방향키로 돌아가고 끝에서 처음으로 이어짐.
/// 값이 바뀌고 잠시(0.6초) 그대로면 TimeCommitted가 한 번 발생 → 돌리는 동안 저장이 계속 일어나지 않음.
/// </summary>
public sealed class TimeWheel : Border
{
    private readonly Wheel _hour, _minute;
    private readonly DispatcherTimer _commit;

    /// <summary>값을 다 돌린 뒤 한 번 (시, 분).</summary>
    public event Action<int, int>? TimeCommitted;

    public int Hour => _hour.Value;
    public int Minute => _minute.Value;

    public TimeWheel(int hour, int minute)
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        BorderBrush = (Brush)Application.Current.FindResource("BorderStrongBrush");
        Background = (Brush)Application.Current.FindResource("InputBrush");
        Padding = new Thickness(6, 0, 6, 0);
        ToolTip = GifPin.Core.Loc.T("scene.wheelTip");

        _hour = new Wheel(24, hour);
        _minute = new Wheel(60, minute);
        var colon = new TextBlock
        {
            Text = ":", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 2),
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_hour);
        row.Children.Add(colon);
        row.Children.Add(_minute);
        Child = row;

        _commit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _commit.Tick += (_, _) => { _commit.Stop(); TimeCommitted?.Invoke(Hour, Minute); };
        _hour.Changed += Restart;
        _minute.Changed += Restart;
    }

    private void Restart()
    {
        _commit.Stop();
        _commit.Start();
    }

    /// <summary>점검용: 바퀴를 n칸 돌림.</summary>
    internal void Spin(bool hour, int steps) => (hour ? _hour : _minute).Step(steps);

    /// <summary>점검용: 기다리지 않고 바로 확정.</summary>
    internal void CommitNow()
    {
        _commit.Stop();
        TimeCommitted?.Invoke(Hour, Minute);
    }

    /// <summary>숫자 바퀴 하나: 위(이전, 흐리게) / 가운데(지금) / 아래(다음, 흐리게).</summary>
    private sealed class Wheel : Border
    {
        private const double DragStepPx = 14;
        private readonly int _count;
        private readonly TextBlock _prev, _cur, _next;
        private double _dragStartY;
        private int _dragStartValue;
        private bool _dragging, _moved;

        public int Value { get; private set; }
        public event Action? Changed;

        public Wheel(int count, int value)
        {
            _count = count;
            Value = ((value % count) + count) % count;
            Width = 30;
            Height = 58;
            Background = Brushes.Transparent; // 빈 곳도 휠·끌기를 받게
            Focusable = true;
            Cursor = Cursors.SizeNS;
            FocusVisualStyle = null;

            var dim = (Brush)Application.Current.FindResource("TextMutedBrush");
            var main = (Brush)Application.Current.FindResource("TextBrush");
            _prev = new TextBlock { FontSize = 10.5, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 };
            _cur = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = main, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
            _next = new TextBlock { FontSize = 10.5, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.7 };
            foreach (var t in new[] { _prev, _cur, _next }) t.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(_prev);
            stack.Children.Add(_cur);
            stack.Children.Add(_next);
            Child = stack;
            Show();

            GotKeyboardFocus += (_, _) => _cur.Foreground = (Brush)Application.Current.FindResource("AccentLightBrush");
            LostKeyboardFocus += (_, _) => _cur.Foreground = main;
        }

        private int Wrap(int v) => ((v % _count) + _count) % _count;

        private void Show()
        {
            _prev.Text = Wrap(Value - 1).ToString("00");
            _cur.Text = Value.ToString("00");
            _next.Text = Wrap(Value + 1).ToString("00");
        }

        public void Step(int steps) => SetValue(Wrap(Value + steps));

        private void SetValue(int v)
        {
            if (v == Value) return;
            Value = v;
            Show();
            Changed?.Invoke();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            Step(e.Delta > 0 ? -1 : 1); // 위로 굴리면 위의 숫자(이전)가 내려옴
            Focus();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            _dragging = true;
            _moved = false;
            _dragStartY = e.GetPosition(this).Y;
            _dragStartValue = Value;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            double dy = e.GetPosition(this).Y - _dragStartY;
            int steps = (int)Math.Round(-dy / DragStepPx); // 위로 끌면 숫자가 올라감
            if (steps != 0) _moved = true;
            SetValue(Wrap(_dragStartValue + steps));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            if (!_moved)
            {
                // 끌지 않고 눌렀으면: 위쪽 숫자를 누르면 하나 전, 아래쪽이면 하나 다음
                double y = e.GetPosition(this).Y;
                if (y < ActualHeight / 3) Step(-1);
                else if (y > ActualHeight * 2 / 3) Step(1);
            }
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up: Step(-1); e.Handled = true; break;
                case Key.Down: Step(1); e.Handled = true; break;
                case Key.PageUp: Step(-10); e.Handled = true; break;
                case Key.PageDown: Step(10); e.Handled = true; break;
            }
        }
    }
}
