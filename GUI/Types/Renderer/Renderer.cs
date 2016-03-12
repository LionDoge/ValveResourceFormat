﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.KeyValues;
using ValveResourceFormat.ResourceTypes;

namespace GUI.Types.Renderer
{
    class Renderer
    {
        // FIX ME: EVERYTHING IS SHITTY JUST SO IT WORKS MAKE IT PRETTY PLS

        bool Loaded = false;

        private uint[] vertexBuffers;
        private uint[] indexBuffers;

        private int vertexShader;
        private int fragmentShader;
        private int shaderProgram;

        private string CurrentFileName;
        private BinaryKV3 data;
        private VBIB block;

        private GLControl meshControl;

        private Camera ActiveCamera;
        private TabControl tabs;

        private List<drawCall> drawCalls = new List<drawCall>();

        private Vector3 MinBounds;
        private Vector3 MaxBounds;

        private int MaxTextureMaxAnisotropy;

        private struct drawCall
        {
            public PrimitiveType primitiveType;
            public uint baseVertex;
            public uint vertexCount;
            public uint startIndex;
            public uint indexCount;
            public uint instanceIndex;   //TODO
            public uint instanceCount;   //TODO
            public float uvDensity;     //TODO
            public string flags;        //TODO
            public Vector3 tintColor;   //TODO
            public string material;
            public int materialID;
            public uint vertexArrayObject;
            public drawBuffer vertexBuffer;
            public DrawElementsType indiceType;
            public drawBuffer indexBuffer;
        }

        private struct drawBuffer
        {
            public uint id;
            public uint offset;
        }

        public Renderer(Resource resource, TabControl mainTabs, string fileName)
        {
            CurrentFileName = fileName;
            block = resource.VBIB;
            data = (BinaryKV3)resource.Blocks[BlockType.DATA];
            tabs = mainTabs;
        }

        public Control createGL()
        {
            meshControl = new GLControl(new OpenTK.Graphics.GraphicsMode(32, 24, 0, 8), 3, 0, OpenTK.Graphics.GraphicsContextFlags.Default);
            meshControl.Dock = DockStyle.Fill;
            meshControl.AutoSize = true;
            meshControl.Load += MeshControl_Load;
            meshControl.Paint += MeshControl_Paint;
            meshControl.Resize += MeshControl_Resize;
            meshControl.MouseEnter += MeshControl_MouseEnter;
            meshControl.MouseLeave += MeshControl_MouseLeave;
            meshControl.GotFocus += MeshControl_GotFocus;
            return meshControl;
        }

        private void MeshControl_GotFocus(object sender, EventArgs e)
        {
            meshControl.MakeCurrent();
            meshControl.SwapBuffers();
        }

        private void MeshControl_MouseLeave(object sender, EventArgs e)
        {
            ActiveCamera.MouseOverRenderArea = false;
        }

        private void MeshControl_MouseEnter(object sender, EventArgs e)
        {
            ActiveCamera.MouseOverRenderArea = true;
        }

        private void MeshControl_Resize(object sender, EventArgs e)
        {
            if (!Loaded)
                return;

            ActiveCamera.SetViewportSize(tabs.Width, tabs.Height);
            int transformLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(transformLoc, false, ref ActiveCamera.ProjectionMatrix);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            meshControl.SwapBuffers();
        }

        private void InitializeInputTick()
        {
            var timer = new System.Timers.Timer();
            timer.Enabled = true;
            timer.Interval = 1000 / 60;
            timer.Elapsed += InputTick;
            timer.Start();
        }

        private void InputTick(object sender, EventArgs e)
        {
            ActiveCamera.HandleInput(Mouse.GetState(), Keyboard.GetState());
        }

        public void CheckOpenGL()
        {
            var x = new Version(GL.GetString(StringName.Version).Split(' ')[0]);
            var y = new Version(3, 3, 0, 0);

            if (x < y)
            {
                Console.WriteLine(string.Format("OpenGL {0} or newer required.", y));
            }

            var extensions = GL.GetString(StringName.Extensions).Split(' ');
            if (extensions.Contains("GL_EXT_texture_filter_anisotropic"))
            {
                MaxTextureMaxAnisotropy = GL.GetInteger((GetPName)ExtTextureFilterAnisotropic.MaxTextureMaxAnisotropyExt);

                Console.WriteLine("MaxTextureMaxAnisotropy: " + MaxTextureMaxAnisotropy);
            }
        }

        private void MeshControl_Load(object sender, System.EventArgs e)
        {
            CheckOpenGL();
            LoadBoundingBox();

            meshControl.MakeCurrent();

            Console.WriteLine("OpenGL version: " + GL.GetString(StringName.Version));
            Console.WriteLine("OpenGL vendor: " + GL.GetString(StringName.Vendor));
            Console.WriteLine("GLSL version: " + GL.GetString(StringName.ShadingLanguageVersion));

            GL.Enable(EnableCap.Texture2D);
            GL.ActiveTexture(TextureUnit.Texture0);

            GL.Enable(EnableCap.DepthTest);

            GL.ClearColor(Color.Black);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            InitializeInputTick();

            ActiveCamera = new Camera(tabs.Width, tabs.Height, MinBounds, MaxBounds);

            Console.WriteLine("Setting up shaders..");

            /* Vertex shader */
            vertexShader = GL.CreateShader(ShaderType.VertexShader);

            string vertexShaderSource = @"
#version 330
 
in vec3 vPosition;
in vec3 vNormal;
in vec2 vTexCoord;

out vec3 vNormalOut;
out vec2 vTexCoordOut;

uniform mat4 projection;
uniform mat4 modelview;

void main()
{
    gl_Position = projection * modelview * vec4(vPosition, 1.0);
    vTexCoordOut = vTexCoord;
}
";

            GL.ShaderSource(vertexShader, vertexShaderSource);
            GL.CompileShader(vertexShader);

            int vsStatus;
            GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out vsStatus);

            if (vsStatus != 1)
            {
                string vsInfo;
                GL.GetShaderInfoLog(vertexShader, out vsInfo);
                throw new Exception("Error setting up Vertex Shader: " + vsInfo);
            }
            else
            {
                Console.WriteLine("Vertex shader compiled succesfully.");
            }

            /* Fragment shader */
            fragmentShader = GL.CreateShader(ShaderType.FragmentShader);

            string fragmentShaderSource = @"
#version 330
 
in vec2 vTexCoordOut;
out vec4 outputColor;
 
uniform sampler2D currentTexture;

void main()
{
    outputColor = texture(currentTexture, vTexCoordOut);
}
";
            GL.ShaderSource(fragmentShader, fragmentShaderSource);
            GL.CompileShader(fragmentShader);

            int fsStatus;
            GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out fsStatus);

            if (fsStatus != 1)
            {
                string fsInfo;
                GL.GetShaderInfoLog(fragmentShader, out fsInfo);
                throw new Exception("Error setting up Fragment Shader: " + fsInfo);
            }
            else
            {
                Console.WriteLine("Fragment shader compiled succesfully.");
            }

            shaderProgram = GL.CreateProgram();
            GL.AttachShader(shaderProgram, vertexShader);
            GL.AttachShader(shaderProgram, fragmentShader);

            GL.LinkProgram(shaderProgram);

            string programInfoLog = GL.GetProgramInfoLog(shaderProgram);
            Console.Write(programInfoLog);

            int linkStatus;
            GL.GetProgram(shaderProgram, GetProgramParameterName.LinkStatus, out linkStatus);

            if (linkStatus != 1)
            {
                string linkInfo;
                GL.GetProgramInfoLog(shaderProgram, out linkInfo);
                throw new Exception("Error linking shaders: " + linkInfo);
            }
            else
            {
                Console.WriteLine("Shaders linked succesfully.");
            }

            GL.UseProgram(shaderProgram);

            GL.ValidateProgram(shaderProgram);

            GL.DetachShader(shaderProgram, vertexShader);
            GL.DeleteShader(vertexShader);

            GL.DetachShader(shaderProgram, fragmentShader);
            GL.DeleteShader(fragmentShader);

            int transformLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(transformLoc, false, ref ActiveCamera.ProjectionMatrix);

            Console.WriteLine("Setting up buffers..");

            vertexBuffers = new uint[block.VertexBuffers.Count];
            indexBuffers = new uint[block.IndexBuffers.Count];

            GL.GenBuffers(block.VertexBuffers.Count, vertexBuffers);
            GL.GenBuffers(block.IndexBuffers.Count, indexBuffers);

            Console.WriteLine(block.VertexBuffers.Count + " vertex buffers");
            Console.WriteLine(block.IndexBuffers.Count + " index buffers");

            for(int i = 0; i < block.VertexBuffers.Count; i++)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffers[i]);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(block.VertexBuffers[i].Count * block.VertexBuffers[i].Size), block.VertexBuffers[i].Buffer, BufferUsageHint.StaticDraw);

                int verticeBufferSize = 0;
                GL.GetBufferParameter(BufferTarget.ArrayBuffer, BufferParameterName.BufferSize, out verticeBufferSize);
                Console.WriteLine("VBuffer size: " + verticeBufferSize);
            }

            for (int i = 0; i < block.IndexBuffers.Count; i++)
            {
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffers[i]);
                GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(block.IndexBuffers[i].Count * block.IndexBuffers[i].Size), block.IndexBuffers[i].Buffer, BufferUsageHint.StaticDraw);

                int indiceBufferSize = 0;
                GL.GetBufferParameter(BufferTarget.ElementArrayBuffer, BufferParameterName.BufferSize, out indiceBufferSize);
                Console.WriteLine("IBuffer size: " + indiceBufferSize);
            }

            Console.WriteLine("Pushed buffers");

            //Prepare drawcalls

            KVObject a = (KVObject)data.Data.Properties["m_sceneObjects"].Value;
            KVObject b = (KVObject)a.Properties["0"].Value;
            KVObject c = (KVObject)b.Properties["m_drawCalls"].Value;

            GL.Enable(EnableCap.Texture2D);
            GL.ActiveTexture(TextureUnit.Texture0);

            for (int i = 0; i < c.Properties.Count; i++)
            {
                KVObject d = (KVObject)c.Properties[i.ToString()].Value;
                var drawCall = new drawCall();

                switch (d.Properties["m_nPrimitiveType"].Value.ToString())
                {
                    case "RENDER_PRIM_TRIANGLES":
                        drawCall.primitiveType = PrimitiveType.Triangles;
                        break;
                    default:
                        throw new Exception("Unknown PrimitiveType in drawCall! (" + d.Properties["m_nPrimitiveType"].Value.ToString() + ")");
                }

                drawCall.baseVertex = Convert.ToUInt32(d.Properties["m_nBaseVertex"].Value);
                drawCall.vertexCount = Convert.ToUInt32(d.Properties["m_nVertexCount"].Value);
                drawCall.startIndex = Convert.ToUInt32(d.Properties["m_nStartIndex"].Value);
                drawCall.indexCount = Convert.ToUInt32(d.Properties["m_nIndexCount"].Value);

                drawCall.material = d.Properties["m_material"].Value.ToString();

                if (!MaterialLoader.materials.ContainsKey(drawCall.material))
                {
                    drawCall.materialID = MaterialLoader.loadMaterial(drawCall.material, CurrentFileName, MaxTextureMaxAnisotropy);
                }
                else
                {
                    drawCall.materialID = MaterialLoader.materials[drawCall.material].textureID;
                }

                KVObject f = (KVObject)d.Properties["m_indexBuffer"].Value;

                var indexBuffer = new drawBuffer();
                indexBuffer.id = Convert.ToUInt32(f.Properties["m_hBuffer"].Value);
                indexBuffer.offset = Convert.ToUInt32(f.Properties["m_nBindOffsetBytes"].Value);
                drawCall.indexBuffer = indexBuffer;

                if (block.IndexBuffers[(int)drawCall.indexBuffer.id].Size == 2) //shopkeeper_vr
                {
                    drawCall.indiceType = DrawElementsType.UnsignedShort;
                }
                else if (block.IndexBuffers[(int)drawCall.indexBuffer.id].Size == 4) //glados
                {
                    drawCall.indiceType = DrawElementsType.UnsignedInt;
                }
                else
                {
                    throw new Exception("Unsupported indice type");
                }

                KVObject g = (KVObject)d.Properties["m_vertexBuffers"].Value;
                KVObject h = (KVObject)g.Properties["0"].Value;

                var vertexBuffer = new drawBuffer();
                vertexBuffer.id = Convert.ToUInt32(h.Properties["m_hBuffer"].Value);
                vertexBuffer.offset = Convert.ToUInt32(h.Properties["m_nBindOffsetBytes"].Value);
                drawCall.vertexBuffer = vertexBuffer;

                GL.GenVertexArrays(1, out drawCall.vertexArrayObject);

                GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffers[drawCall.vertexBuffer.id]);
                GL.BindVertexArray(drawCall.vertexArrayObject);

                var curVertexBuffer = block.VertexBuffers[(int)drawCall.vertexBuffer.id];
                var texcoordSet = false;
                foreach (var attribute in curVertexBuffer.Attributes)
                {
                    switch (attribute.Name)
                    {
                        case "POSITION":
                            GL.EnableClientState(ArrayCap.VertexArray);
                            switch (attribute.Type)
                            {
                                case DXGI_FORMAT.R32G32B32_FLOAT:
                                    int posAttrib = GL.GetAttribLocation(shaderProgram, "vPosition");
                                    GL.EnableVertexAttribArray(posAttrib);
                                    GL.VertexAttribPointer(posAttrib, 3, VertexAttribPointerType.Float, false, (int)curVertexBuffer.Size, (IntPtr)attribute.Offset);
                                    break;
                                default:
                                    throw new Exception("Unknown position format " + attribute.Type);
                            }
                            break;
                        case "NORMAL": // TODO: shader support for normals
                            GL.EnableClientState(ArrayCap.NormalArray);
                            switch (attribute.Type)
                            {
                                case DXGI_FORMAT.R32G32B32_FLOAT:
                                    GL.NormalPointer(NormalPointerType.Float, (int)curVertexBuffer.Size, (IntPtr)attribute.Offset);
                                    break;
                                case DXGI_FORMAT.R8G8B8A8_UNORM:
                                    GL.NormalPointer(NormalPointerType.Short, (int)curVertexBuffer.Size, (IntPtr)attribute.Offset);
                                    break;
                                default:
                                    throw new Exception("Unsupported normal format " + attribute.Type);
                            }
                            break;
                        case "TEXCOORD":
                            if (texcoordSet) { break; } // Ignore second set of texcoords 
                            GL.EnableClientState(ArrayCap.TextureCoordArray);
                            int texCoordAttrib = GL.GetAttribLocation(shaderProgram, "vTexCoord");
                            GL.EnableVertexAttribArray(texCoordAttrib);
                            switch (attribute.Type)
                            {
                                case DXGI_FORMAT.R32G32_FLOAT:
                                    GL.VertexAttribPointer(texCoordAttrib, 2, VertexAttribPointerType.Float, false, (int)curVertexBuffer.Size, (IntPtr)attribute.Offset);
                                    break;
                                case DXGI_FORMAT.R16G16_FLOAT:
                                    GL.VertexAttribPointer(texCoordAttrib, 2, VertexAttribPointerType.HalfFloat, false, (int)curVertexBuffer.Size, (IntPtr)attribute.Offset);
                                    break;
                                default:
                                    throw new Exception("Unsupported texcoord format " + attribute.Type);
                            }
                            texcoordSet = true;
                            break;
                        case "TANGENT":
                            switch (attribute.Type)
                            {
                                case DXGI_FORMAT.R32G32B32A32_FLOAT:
                                    //TODO
                                    break;
                                default:
                                    throw new Exception("Unsupported tangent format " + attribute.Type);
                            }
                            break;
                        case "BLENDINDICES":
                            switch (attribute.Type)
                            {
                                case DXGI_FORMAT.R8G8B8A8_UINT:
                                case DXGI_FORMAT.R16G16_SINT:
                                case DXGI_FORMAT.R16G16B16A16_SINT:
                                    //TODO
                                    break;
                                default:
                                    throw new Exception("Unsupported blend indices format " + attribute.Type);
                            }
                            break;
                        case "BLENDWEIGHT":
                            switch (attribute.Type)
                            {
                                case DXGI_FORMAT.R16G16_UNORM:
                                case DXGI_FORMAT.R8G8B8A8_UINT:
                                case DXGI_FORMAT.R8G8B8A8_UNORM:
                                    //TODO
                                    break;
                                default:
                                    throw new Exception("Unsupported blend weight format " + attribute.Type);
                            }
                            break;
                    }
                }

                GL.BindVertexArray(0);
                GL.EnableVertexAttribArray(drawCall.vertexArrayObject);

                drawCalls.Add(drawCall);
            }

            int projectionLoc = GL.GetUniformLocation(shaderProgram, "projection");
            GL.UniformMatrix4(projectionLoc, false, ref ActiveCamera.ProjectionMatrix);

            Loaded = true;
        }

        private void MeshControl_Paint(object sender, PaintEventArgs e)
        {
            if (!Loaded)
                return;

            ActiveCamera.Tick();

            int modelviewLoc = GL.GetUniformLocation(shaderProgram, "modelview");
            GL.UniformMatrix4(modelviewLoc, false, ref ActiveCamera.CameraViewMatrix);

            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            foreach (drawCall call in drawCalls)
            {
                if(call.materialID != 1) // Don't do material lookups on error texture
                {
                    if (MaterialLoader.materials[call.material].intParams.ContainsKey("F_TRANSLUCENT") && MaterialLoader.materials[call.material].intParams["F_TRANSLUCENT"] == 1)
                    {
                        GL.Enable(EnableCap.Blend);
                        GL.BlendFunc(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha);
                    }
                    else if (MaterialLoader.materials[call.material].intParams.ContainsKey("F_ALPHA_TEST") && MaterialLoader.materials[call.material].intParams["F_ALPHA_TEST"] == 1)
                    {
                        GL.Enable(EnableCap.AlphaTest);
                        GL.AlphaFunc(AlphaFunction.Greater, MaterialLoader.materials[call.material].floatParams["g_flAlphaTestReference"]);
                    }
                }
                
                GL.BindVertexArray(call.vertexArrayObject);
                GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffers[call.vertexBuffer.id]);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffers[call.indexBuffer.id]);
                GL.BindTexture(TextureTarget.Texture2D, call.materialID);
                GL.DrawElements(call.primitiveType, (int) call.indexCount, call.indiceType, IntPtr.Zero);

                GL.Disable(EnableCap.AlphaTest);
                GL.Disable(EnableCap.Blend);
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            // Only needed when debugging if something doesnt work, causes high CPU
            /*
            var error = GL.GetError();

            if (error != ErrorCode.NoError)
            {
                Console.WriteLine(error);
            }
            */

            meshControl.SwapBuffers();
            meshControl.Invalidate();
        }

        private void LoadBoundingBox()
        {
            var a = (KVObject)data.Data.Properties["m_sceneObjects"].Value;
            var b = (KVObject)a.Properties["0"].Value;
            var minBounds = (KVObject)b.Properties["m_vMinBounds"].Value;
            var maxBounds = (KVObject)b.Properties["m_vMaxBounds"].Value;

            MaxBounds.X = (float)Convert.ToDouble(maxBounds.Properties["0"].Value);
            MinBounds.X = (float)Convert.ToDouble(minBounds.Properties["0"].Value);
            MaxBounds.Y = (float)Convert.ToDouble(maxBounds.Properties["1"].Value);
            MinBounds.Y = (float)Convert.ToDouble(minBounds.Properties["1"].Value);
            MaxBounds.Z = (float)Convert.ToDouble(maxBounds.Properties["2"].Value);
            MinBounds.Z = (float)Convert.ToDouble(minBounds.Properties["2"].Value);
        }
    }
}
