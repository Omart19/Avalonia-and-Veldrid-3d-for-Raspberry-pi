using Avalonia;
using Avalonia.Input;

namespace VeldridSTLViewer
{
    public class InputState
    {
        public Point MousePosition { get; private set; }
        public Point MouseDelta { get; private set; }
        private readonly bool[] _mouseDown = new bool[256]; // Large enough for all mouse buttons
        private readonly bool[] _keyDown = new bool[256];   // Large enough for all keys
        private Point _previousMousePosition;

        public void UpdateMousePosition(Point position)
        {
            MouseDelta = position - _previousMousePosition;
            _previousMousePosition = position;
            MousePosition = position;
        }

        // Use MouseButton instead of PointerMouseButton
        public bool IsMouseDown(MouseButton button)
        {
            return _mouseDown[(int)button];
        }

        public bool IsKeyDown(Key key)
        {
            return _keyDown[(int)key];
        }

        // Use MouseButton instead of PointerMouseButton
        public void SetMouseDown(MouseButton button, bool pressed)
        {
            _mouseDown[(int)button] = pressed;
        }

        public void SetKeyDown(Key key, bool pressed)
        {
            _keyDown[(int)key] = pressed;
        }
    }
}