using Avalonia.Input;
using System.Numerics;
using VeldridSTLViewer;

namespace VeldridSTLViewer
{
    public class CameraController
    {
        public Matrix4x4 ViewMatrix { get; private set; } = Matrix4x4.Identity;
        public Matrix4x4 ProjectionMatrix { get; private set; } = Matrix4x4.Identity;
        private Vector3 _cameraPosition = new Vector3(0, 0, 5);
        private float _yaw = 0f;
        private float _pitch = 0f;
        private float _moveSpeed = 40.0f;
        private float _rotationSpeed = 0.01f;
        private float _aspectRatio;
        public CameraController(float aspectRatio)
        {
            _aspectRatio = aspectRatio;
            UpdateViewMatrix();
            UpdateProjectionMatrix();
        }
        public void SetCameraPosition(Vector3 position)
        {
            _cameraPosition = position;
            UpdateViewMatrix();
        }
        public void SetCameraRotation(float yaw, float pitch)
        {
            _yaw = yaw;
            _pitch = pitch;
            UpdateViewMatrix();
        }
        public void Update(double deltaTime, InputState input)
        {
            float deltaSeconds = (float)deltaTime;
            Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
            if (input.IsKeyDown(Key.W))
                _cameraPosition += Vector3.Transform(-Vector3.UnitY, rotation) * _moveSpeed * deltaSeconds;
            if (input.IsKeyDown(Key.S))
                _cameraPosition += Vector3.Transform(Vector3.UnitY, rotation) * _moveSpeed * deltaSeconds;
            if (input.IsKeyDown(Key.A))
                _cameraPosition += Vector3.Transform(Vector3.UnitX, rotation) * _moveSpeed * deltaSeconds;
            if (input.IsKeyDown(Key.D))
                _cameraPosition += Vector3.Transform(-Vector3.UnitX, rotation) * _moveSpeed * deltaSeconds;
            if (input.IsMouseDown(MouseButton.Left))
            {
                _yaw += (float)input.MouseDelta.X * _rotationSpeed;
                _pitch -= (float)input.MouseDelta.Y * _rotationSpeed;
                _pitch = Math.Clamp(_pitch, -MathF.PI / 2f, MathF.PI / 2f);
                // Optionally, clamp pitch here if desired.
            }
            UpdateViewMatrix();
        }
        public void UpdateAspectRatio(float aspectRatio)
        {
            _aspectRatio = aspectRatio;
            UpdateProjectionMatrix();
        }
        private void UpdateViewMatrix()
        {
            // Compute forward direction.
            var rotation = Matrix4x4.CreateFromYawPitchRoll(0, _pitch, _yaw);
            Vector3 forward = Vector3.Transform(-Vector3.UnitY, rotation);
            Vector3 cameraTarget = _cameraPosition + forward;
            // FIX: Always use world up.
            Vector3 cameraUp = Vector3.Transform(Vector3.UnitZ, rotation);
            ViewMatrix = Matrix4x4.CreateLookAt(_cameraPosition, cameraTarget, cameraUp);
            Console.WriteLine($"CameraPosition :{_cameraPosition} CameraTarget :{cameraTarget} CameraUp :{cameraUp}");
        }
        private void UpdateProjectionMatrix()
        {
            // Use a 60° FOV and far clip of 1000.
            ProjectionMatrix = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, _aspectRatio, 0.5f, 1000f);
        }
        public Matrix4x4[] GetMVPMatrices(Matrix4x4 modelMatrix)
        {
            return new Matrix4x4[] { modelMatrix, ViewMatrix, ProjectionMatrix };
        }
    }
}
