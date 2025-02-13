//CameraController.cs
using System;
using System.Numerics;
using Avalonia.Input; // For Key and Pointer events

namespace VeldridSTLViewer
{
    public class CameraController
    {
        public Matrix4x4 ViewMatrix { get; private set; } = Matrix4x4.Identity;
        public Matrix4x4 ProjectionMatrix { get; private set; } = Matrix4x4.Identity;

        private Vector3 _cameraPosition = new Vector3(0, 0, 5);
        private float _yaw = 0f;
        private float _pitch = 0f;
        private float _moveSpeed = 2.0f; // Adjust as needed
        private float _rotationSpeed = 0.01f; // Adjust
        private float _aspectRatio;

        public CameraController(float aspectRatio)
        {
            _aspectRatio = aspectRatio;
            UpdateViewMatrix(); // Initialize the view matrix
            UpdateProjectionMatrix(); // Initialize proj matrix
        }

        // Set initial camera position
        public void SetCameraPosition(Vector3 position)
        {
            _cameraPosition = position;
            UpdateViewMatrix(); // Recalculate whenever position changes
        }

        // Set initial camera rotation
        public void SetCameraRotation(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = pitch;
            UpdateViewMatrix(); // Recalculate whenever rotation changes.
        }

        public void Update(double deltaTime, InputState input)
        {
            float deltaSeconds = (float)deltaTime;

            // Camera Movement (WASD) - Corrected Transformation
            Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
            if (input.IsKeyDown(Key.W))
            {
                _cameraPosition += Vector3.Transform(-Vector3.UnitZ, rotation) * _moveSpeed * deltaSeconds; // Move FORWARD
            }
            if (input.IsKeyDown(Key.S))
            {
                _cameraPosition += Vector3.Transform(Vector3.UnitZ, rotation) * _moveSpeed * deltaSeconds;  // Move BACKWARD
            }
            if (input.IsKeyDown(Key.A))
            {
                _cameraPosition += Vector3.Transform(-Vector3.UnitX, rotation) * _moveSpeed * deltaSeconds; // Move LEFT
            }
            if (input.IsKeyDown(Key.D))
            {
                _cameraPosition += Vector3.Transform(Vector3.UnitX, rotation) * _moveSpeed * deltaSeconds;  // Move RIGHT
            }

            if (input.IsMouseDown(MouseButton.Left))
            {
                // Camera Rotation (Mouse) - Casts to float are correct
                _yaw += (float)input.MouseDelta.X * _rotationSpeed;
                _pitch -= (float)input.MouseDelta.Y * _rotationSpeed; // Inverted for typical controls
                _pitch = Math.Clamp(_pitch, -MathF.PI / 2f, MathF.PI / 2f); // Limit pitch, prevent flipping
            }

            UpdateViewMatrix();  // Recalculate view matrix
        }

        public void UpdateAspectRatio(float aspectRatio)
        {
            _aspectRatio = aspectRatio;
            UpdateProjectionMatrix();
        }
        private void UpdateViewMatrix()
        {
            var rotation = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
            Vector3 cameraTarget = _cameraPosition + Vector3.Transform(-Vector3.UnitZ, rotation);
            Vector3 cameraUp = Vector3.Transform(Vector3.UnitY, rotation);
            ViewMatrix = Matrix4x4.CreateLookAt(_cameraPosition, cameraTarget, cameraUp);
        }


        private void UpdateProjectionMatrix()
        {
            ProjectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, _aspectRatio, 0.1f, 100f);
        }

        public Matrix4x4[] GetMVPMatrices(Matrix4x4 modelMatrix)
        {
            return new Matrix4x4[] { modelMatrix, ViewMatrix, ProjectionMatrix };
        }
    }
}