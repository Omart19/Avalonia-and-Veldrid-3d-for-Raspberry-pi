// using Avalonia.Controls;
// using Avalonia.Rendering;
// using System;
// using System.IO;
// using System.Numerics;
// using Veldrid;
// using Veldrid.StartupUtilities;
// using Veldrid.SPIRV;
// using System.Text;
// using System.Runtime.InteropServices;
// using Avalonia.Threading;
// using Avalonia;

// namespace VeldridSTLViewer
// {
//     public class VeldridPanel : Control
//     {
//         private GraphicsDevice _graphicsDevice;
//         private CommandList _commandList;
//         private DeviceBuffer _vertexBuffer;
//         private DeviceBuffer _indexBuffer;
//         private Pipeline _pipeline;
//         private Shader[] _shaders;
//         private ResourceSet _resourceSet;
//         private DeviceBuffer _projectionBuffer;
//         private Framebuffer _framebuffer;
//         private Texture _colorTarget;
//         private ResourceLayout _resourceLayout;
//         private Matrix4x4 _modelMatrix;
//         private DeviceBuffer _mvpBuffer;
//         private ResourceLayout _mvpLayout;
//         private ResourceSet _mvpResourceSet;
//         private uint _indexCount; // Store index count for rendering
//         private DispatcherTimer _renderTimer;

//                 private const string VertexCode = @"
// #version 450
// layout(location = 0) in vec3 Position;
// layout(location = 1) in vec3 Normal;
// layout(location = 0) out vec3 v_Normal;
// layout(set = 0, binding = 0) uniform MVP
// {
//     mat4 Model;
//     mat4 View;
//     mat4 Projection;
// };
// void main()
// {
//     gl_Position = Projection * View * Model * vec4(Position, 1.0);
//     v_Normal = mat3(Model) * Normal;
// }";

//         private const string FragmentCode = @"
// #version 450
// layout(location = 0) in vec3 v_Normal;
// layout(location = 0) out vec4 fsout_Color;
// void main()
// {
//     vec3 lightDir = normalize(vec3(0.5, 1.0, 0.5));
//     float brightness = max(dot(normalize(v_Normal), lightDir), 0.2);
//     fsout_Color = vec4(vec3(brightness), 1.0);
// }";


//         public VeldridPanel()
//         {
//             this.Initialized += OnInitialized;
//         }

//         private void OnInitialized(object sender, EventArgs e)
//         {
//             InitializeGraphics();
//             LoadResources();
//             SetupRenderTimer();
//         }

//         private void SetupRenderTimer()
//     {
//         _renderTimer = new DispatcherTimer();
//         _renderTimer.Interval = TimeSpan.FromMilliseconds(16); // Approximately 60 FPS
//         _renderTimer.Tick += (sender, e) => Render();
//         _renderTimer.Start();
//     }

//         private void StartRendering()
//         {
//             var renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
//             renderTimer.Tick += (s, e) => Render();
//             renderTimer.Start();
//         }

//         private void InitializeGraphics()
//         {
//             GraphicsDeviceOptions options = new GraphicsDeviceOptions
//             {
//                 PreferStandardClipSpaceYDirection = true,
//                 PreferDepthRangeZeroToOne = true,
//                 SyncToVerticalBlank = true,
//                 ResourceBindingModel = ResourceBindingModel.Improved
//             };

//             _graphicsDevice = GraphicsDevice.CreateVulkan(options);

//             _colorTarget = _graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription(
//                 800, 600, 1, 1, 1, Veldrid.PixelFormat.R32_G32_B32_A32_Float, TextureUsage.GenerateMipmaps | TextureUsage.Sampled, TextureType.Texture2D));

//             var depthTarget = _graphicsDevice.ResourceFactory.CreateTexture(new TextureDescription(
//                 800, 600, 1, 1, 1, Veldrid.PixelFormat.R32_Float, TextureUsage.GenerateMipmaps, TextureType.Texture2D));

//             _framebuffer = _graphicsDevice.ResourceFactory.CreateFramebuffer(new FramebufferDescription(null, _colorTarget, depthTarget));
//         }

//         private void LoadResources()
//         {
//             ResourceFactory factory = _graphicsDevice.ResourceFactory;

//             var (vertices, indices) = LoadSTL("model.stl");
//             _indexCount = (uint)indices.Length; // Store index count

//             _vertexBuffer = factory.CreateBuffer(new BufferDescription(
//                 (uint)(vertices.Length * VertexPositionNormal.SizeInBytes),
//                 BufferUsage.VertexBuffer));
//             _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, vertices);

//             _indexBuffer = factory.CreateBuffer(new BufferDescription(
//                 (uint)(indices.Length * sizeof(ushort)),
//                 BufferUsage.IndexBuffer));
//             _graphicsDevice.UpdateBuffer(_indexBuffer, 0, indices);

//             VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
//                 new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
//                 new VertexElementDescription("Normal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3));

//             ShaderDescription vertexShaderDesc = new ShaderDescription(
//                 ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertexCode), "main");
//             ShaderDescription fragmentShaderDesc = new ShaderDescription(
//                 ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentCode), "main");
//             _shaders = factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);

//             _mvpBuffer = factory.CreateBuffer(new BufferDescription(
//                 64 * sizeof(float), BufferUsage.UniformBuffer | BufferUsage.Dynamic));

//             _resourceSet = factory.CreateResourceSet(new ResourceSetDescription(
//                 factory.CreateResourceLayout(new ResourceLayoutDescription(
//                     new ResourceLayoutElementDescription("MVP", ResourceKind.UniformBuffer, ShaderStages.Vertex))),
//                 _mvpBuffer));

//             _pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
//                 BlendStateDescription.SingleOverrideBlend,
//                 new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
//                 new RasterizerStateDescription(FaceCullMode.Back, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
//                 PrimitiveTopology.TriangleList,
//                 new ShaderSetDescription(new[] { vertexLayout }, _shaders),
//                 new ResourceLayout[] { _mvpLayout },
//                 _framebuffer.OutputDescription));
//         }

//         private void UpdateMVP()
// {
//     Matrix4x4 model = Matrix4x4.CreateTranslation(0, 0, 0); // Simple model matrix
//     Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -3), Vector3.Zero, Vector3.UnitY);
//     Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView((float)Math.PI / 4, 800f / 600f, 0.1f, 100f);
//     Matrix4x4 mvp = model * view * projection;
//     _graphicsDevice.UpdateBuffer(_mvpBuffer, 0, ref mvp);
// }

// private void Render()
// {
//     UpdateMVP(); // Update matrices
//     _commandList.Begin();
//     _commandList.SetFramebuffer(_framebuffer);
//     _commandList.ClearColorTarget(0, RgbaFloat.Black); // Change color for debugging
//     _commandList.ClearDepthStencil(1.0f);
//     _commandList.SetVertexBuffer(0, _vertexBuffer);
//     _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
//     _commandList.SetPipeline(_pipeline);
//     _commandList.SetGraphicsResourceSet(0, _resourceSet);
//     _commandList.DrawIndexed(
//                 indexCount: (uint)(_indexBuffer.SizeInBytes / sizeof(ushort)),
//                 instanceCount: 1,
//                 indexStart: 0,
//                 vertexOffset: 0,
//                 instanceStart: 0);
//     _commandList.End();
//     _graphicsDevice.SubmitCommands(_commandList);
//     _graphicsDevice.WaitForIdle();
//     this.InvalidateVisual(); // Trigger redraw if needed
// }

//         protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
//         {
//             _framebuffer.Dispose();
//             _colorTarget.Dispose();
//             _graphicsDevice.Dispose();
//             base.OnDetachedFromVisualTree(e);
//         }

//         private (VertexPositionNormal[], ushort[]) LoadSTL(string path)
//         {
//             List<VertexPositionNormal> vertices = new List<VertexPositionNormal>();
//             List<ushort> indices = new List<ushort>();

//             using (FileStream fs = File.OpenRead(path))
//             using (BinaryReader reader = new BinaryReader(fs))
//             {
//                 reader.ReadBytes(80); // Skip the header
//                 int triangleCount = reader.ReadInt32();
//                 for (int i = 0; i < triangleCount; i++)
//                 {
//                     Vector3 normal = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
//                     for (int j = 0; j < 3; j++)
//                     {
//                         Vector3 position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
//                         vertices.Add(new VertexPositionNormal { Position = position, Normal = normal });
//                         indices.Add((ushort)(vertices.Count - 1));
//                     }
//                     reader.ReadBytes(2); // Skip attribute byte count
//                 }
//             }

//             return (vertices.ToArray(), indices.ToArray());
//         }
//     }

//     // public struct VertexPositionNormal
//     // {
//     //     public Vector3 Position;
//     //            public Vector3 Normal;

//     //     public VertexPositionNormal(Vector3 position, Vector3 normal)
//     //     {
//     //         Position = position;
//     //         Normal = normal;
//     //     }

//     //     public static readonly uint SizeInBytes = (uint)System.Runtime.InteropServices.Marshal.SizeOf<VertexPositionNormal>();
//     // }
// }

