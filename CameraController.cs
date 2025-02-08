using System;
using System.Numerics;
using Avalonia.Input; // For Key and Pointer events

namespace VeldridSTLViewer
{
    public class CameraController
    {
        public Matrix4x4 ViewMatrix { get; private set; } = Matrix4x4.Identity;
        public Matrix4x4 ProjectionMatrix { get; private set; } = Matrix4x4.Identity;
        public Matrix4x4 ModelMatrix { get; private set; } = Matrix4x4.Identity;

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

        public void Update(double deltaTime, InputState input)
        {
            float deltaSeconds = (float)deltaTime;

            // Camera Movement (WASD)
            if (input.IsKeyDown(Key.W))
            {
                _cameraPosition += Vector3.Transform(Vector3.UnitZ, Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0)) * _moveSpeed * deltaSeconds;
            }
            if (input.IsKeyDown(Key.S))
            {
                _cameraPosition -= Vector3.Transform(Vector3.UnitZ, Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0)) * _moveSpeed * deltaSeconds;
            }
            if (input.IsKeyDown(Key.A))
            {
                _cameraPosition += Vector3.Transform(Vector3.UnitX, Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0)) * _moveSpeed * deltaSeconds;
            }
            if (input.IsKeyDown(Key.D))
            {
                _cameraPosition -= Vector3.Transform(Vector3.UnitX, Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0)) * _moveSpeed * deltaSeconds;
            }

            if (input.IsMouseDown(MouseButton.Left)) // Corrected: Use MouseButton
            {
                // Camera Rotation (Mouse)
                _yaw += (float)input.MouseDelta.X * _rotationSpeed;  // Cast to float
                _pitch -= (float)input.MouseDelta.Y * _rotationSpeed; // Cast to float, inverted
                _pitch = Math.Clamp(_pitch, -1.5f, 1.5f); // Limit pitch
            }
            UpdateViewMatrix(); // Recalculate view matrix
        }

        public void UpdateAspectRatio(float aspectRatio)
        {
            _aspectRatio = aspectRatio;
            UpdateProjectionMatrix();

        }
        private void UpdateViewMatrix()
        {
            var rotation = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
            Vector3 cameraTarget = Vector3.Transform(Vector3.Zero, rotation); // Target is always at origin
            Vector3 cameraUp = Vector3.Transform(Vector3.UnitY, rotation);

            ViewMatrix = Matrix4x4.CreateLookAt(_cameraPosition, cameraTarget + _cameraPosition, cameraUp);
        }

        private void UpdateProjectionMatrix()
        {
            ProjectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, _aspectRatio, 0.1f, 100f);

        }
        public Matrix4x4[] GetMVPMatrices(Matrix4x4 modelMatrix) // Takes model matrix as parameter
        {
            return new Matrix4x4[] { modelMatrix, ViewMatrix, ProjectionMatrix };
        }

        // Method to set the model matrix.  Important for later.
        public void SetModelMatrix(Matrix4x4 modelMatrix)
        {
            ModelMatrix = modelMatrix;
        }
    }
}