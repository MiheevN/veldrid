﻿using static Veldrid.OpenGLBinding.OpenGLNative;
using static Veldrid.OpenGL.OpenGLUtil;
using System;
using Veldrid.OpenGLBinding;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Veldrid.OpenGL.EAGL;
using static Veldrid.OpenGL.EGL.EGLNative;
using NativeLibraryLoader;
using System.Runtime.CompilerServices;
using NativeLibrary = NativeLibraryLoader.NativeLibrary;
using Veldrid.OpenGL.EntryList;

namespace Veldrid.OpenGL
{
    internal unsafe class OpenGLGraphicsDevice : GraphicsDevice
    {
        private ResourceFactory _resourceFactory;
        private string _deviceName;
        private string _vendorName;
        private string _version;
        private string _shadingLanguageVersion;
        private GraphicsApiVersion _apiVersion;
        private GraphicsBackend _backendType;
        private GraphicsDeviceFeatures _features;
        private uint _vao;
        private readonly ConcurrentQueue<OpenGLDeferredResource> _resourcesToDispose
            = new ConcurrentQueue<OpenGLDeferredResource>();
        private IntPtr _glContext;
        private Action<IntPtr> _makeCurrent;
        private Func<IntPtr> _getCurrentContext;
        private Action<IntPtr> _deleteContext;
        private Action _swapBuffers;
        private Action<bool> _setSyncToVBlank;
        private OpenGLSwapchainFramebuffer _swapchainFramebuffer;
        private OpenGLTextureSamplerManager _textureSamplerManager;
        private OpenGLCommandExecutor _commandExecutor;
        private DebugProc _debugMessageCallback;
        private OpenGLExtensions _extensions;
        private bool _isDepthRangeZeroToOne;
        private BackendInfoOpenGL _openglInfo;

        private TextureSampleCount _maxColorTextureSamples;
        private uint _maxTextureSize;
        private uint _maxTexDepth;
        private uint _maxTexArrayLayers;
        private uint _minUboOffsetAlignment;
        private uint _minSsboOffsetAlignment;

        private readonly StagingMemoryPool _stagingMemoryPool = new StagingMemoryPool();
        private Queue<ExecutionThreadWorkItem> _workItems;
        private ExecutionThread _executionThread;
        private readonly object _commandListDisposalLock = new object();
        private readonly Dictionary<OpenGLCommandList, int> _submittedCommandListCounts
            = new Dictionary<OpenGLCommandList, int>();
        private readonly HashSet<OpenGLCommandList> _commandListsToDispose = new HashSet<OpenGLCommandList>();

        private readonly object _mappedResourceLock = new object();
        private readonly Dictionary<MappedResourceCacheKey, MappedResourceInfo> _mappedResources
            = new Dictionary<MappedResourceCacheKey, MappedResourceInfo>();

        private readonly object _resetEventsLock = new object();
        private readonly List<ManualResetEvent[]> _resetEvents = new List<ManualResetEvent[]>();
        private Swapchain _mainSwapchain;

        private bool _syncToVBlank;

        public override string DeviceName => _deviceName;

        public override string VendorName => _vendorName;

        public override GraphicsApiVersion ApiVersion => _apiVersion;

        public override GraphicsBackend BackendType => _backendType;

        public override bool IsUvOriginTopLeft => false;

        public override bool IsDepthRangeZeroToOne => _isDepthRangeZeroToOne;

        public override bool IsClipSpaceYInverted => false;

        public override ResourceFactory ResourceFactory => _resourceFactory;

        public OpenGLExtensions Extensions => _extensions;

        public override Swapchain MainSwapchain => _mainSwapchain;

        public override bool SyncToVerticalBlank
        {
            get => _syncToVBlank;
            set
            {
                if (_syncToVBlank != value)
                {
                    _syncToVBlank = value;
                    _executionThread.SetSyncToVerticalBlank(value);
                }
            }
        }

        public string Version => _version;

        public string ShadingLanguageVersion => _shadingLanguageVersion;

        public OpenGLTextureSamplerManager TextureSamplerManager => _textureSamplerManager;

        public override GraphicsDeviceFeatures Features => _features;

        public StagingMemoryPool StagingMemoryPool => _stagingMemoryPool;

        public OpenGLGraphicsDevice(
            GraphicsDeviceOptions options,
            OpenGLPlatformInfo platformInfo,
            uint width,
            uint height)
        {
            Init(options, platformInfo, width, height, true);
        }

        private void Init(
            GraphicsDeviceOptions options,
            OpenGLPlatformInfo platformInfo,
            uint width,
            uint height,
            bool loadFunctions)
        {
            _syncToVBlank = options.SyncToVerticalBlank;
            _glContext = platformInfo.OpenGLContextHandle;
            _makeCurrent = platformInfo.MakeCurrent;
            _getCurrentContext = platformInfo.GetCurrentContext;
            _deleteContext = platformInfo.DeleteContext;
            _swapBuffers = platformInfo.SwapBuffers;
            _setSyncToVBlank = platformInfo.SetSyncToVerticalBlank;
            LoadGetString(_glContext, platformInfo.GetProcAddress);
            _version = Util.GetString(glGetString(StringName.Version));
            _shadingLanguageVersion = Util.GetString(glGetString(StringName.ShadingLanguageVersion));
            _vendorName = Util.GetString(glGetString(StringName.Vendor));
            _deviceName = Util.GetString(glGetString(StringName.Renderer));
            _backendType = _version.StartsWith("OpenGL ES") ? GraphicsBackend.OpenGLES : GraphicsBackend.OpenGL;

            LoadAllFunctions(_glContext, platformInfo.GetProcAddress, _backendType == GraphicsBackend.OpenGLES);

            int majorVersion, minorVersion;
            glGetIntegerv(GetPName.MajorVersion, &majorVersion);
            CheckLastError();
            glGetIntegerv(GetPName.MinorVersion, &minorVersion);
            CheckLastError();

            GraphicsApiVersion.TryParseGLVersion(_version, out _apiVersion);
            if (_apiVersion.Major != majorVersion ||
                _apiVersion.Minor != minorVersion)
            {
                // This mismatch should never be hit in valid OpenGL implementations.
                _apiVersion = new GraphicsApiVersion(majorVersion, minorVersion, 0, 0);
            }

            int extensionCount;
            glGetIntegerv(GetPName.NumExtensions, &extensionCount);
            CheckLastError();

            HashSet<string> extensions = new HashSet<string>();
            for (uint i = 0; i < extensionCount; i++)
            {
                byte* extensionNamePtr = glGetStringi(StringNameIndexed.Extensions, i);
                CheckLastError();
                if (extensionNamePtr != null)
                {
                    string extensionName = Util.GetString(extensionNamePtr);
                    extensions.Add(extensionName);
                }
            }

            _extensions = new OpenGLExtensions(extensions, _backendType, majorVersion, minorVersion);

            bool drawIndirect = _extensions.DrawIndirect || _extensions.MultiDrawIndirect;
            _features = new GraphicsDeviceFeatures(
                computeShader: _extensions.ComputeShaders,
                geometryShader: _extensions.GeometryShader,
                tessellationShaders: _extensions.TessellationShader,
                multipleViewports: _extensions.ARB_ViewportArray,
                samplerLodBias: _backendType == GraphicsBackend.OpenGL,
                drawBaseVertex: _extensions.DrawElementsBaseVertex,
                drawBaseInstance: _extensions.GLVersion(4, 2),
                drawIndirect: drawIndirect,
                drawIndirectBaseInstance: drawIndirect,
                fillModeWireframe: _backendType == GraphicsBackend.OpenGL,
                samplerAnisotropy: _extensions.AnisotropicFilter,
                depthClipDisable: _backendType == GraphicsBackend.OpenGL,
                texture1D: _backendType == GraphicsBackend.OpenGL,
                independentBlend: _extensions.IndependentBlend,
                structuredBuffer: _extensions.StorageBuffers,
                subsetTextureView: _extensions.ARB_TextureView,
                commandListDebugMarkers: _extensions.KHR_Debug || _extensions.EXT_DebugMarker,
                bufferRangeBinding: _extensions.ARB_uniform_buffer_object,
                shaderFloat64: _extensions.ARB_GpuShaderFp64);

            int uboAlignment;
            glGetIntegerv(GetPName.UniformBufferOffsetAlignment, &uboAlignment);
            CheckLastError();
            _minUboOffsetAlignment = (uint)uboAlignment;

            if (_features.StructuredBuffer)
            {
                int ssboAlignment;
                glGetIntegerv(GetPName.ShaderStorageBufferOffsetAlignment, &ssboAlignment);
                CheckLastError();
                _minSsboOffsetAlignment = (uint)ssboAlignment;
            }

            _resourceFactory = new OpenGLResourceFactory(this);

            uint vao;
            glGenVertexArrays(1, &vao);
            CheckLastError();
            _vao = vao;

            glBindVertexArray(_vao);
            CheckLastError();

            if (options.Debug && (_extensions.KHR_Debug || _extensions.ARB_DebugOutput))
            {
                EnableDebugCallback();
            }

            bool backbufferIsSrgb = ManualSrgbBackbufferQuery();

            PixelFormat swapchainFormat;
            if (options.SwapchainSrgbFormat && (backbufferIsSrgb || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)))
            {
                swapchainFormat = PixelFormat.B8_G8_R8_A8_UNorm_SRgb;
            }
            else
            {
                swapchainFormat = PixelFormat.B8_G8_R8_A8_UNorm;
            }

            _swapchainFramebuffer = new OpenGLSwapchainFramebuffer(
                width,
                height,
                swapchainFormat,
                options.SwapchainDepthFormat,
                swapchainFormat != PixelFormat.B8_G8_R8_A8_UNorm_SRgb);

            // Set miscellaneous initial states.
            if (_backendType == GraphicsBackend.OpenGL)
            {
                glEnable(EnableCap.TextureCubeMapSeamless);
                CheckLastError();
            }

            _textureSamplerManager = new OpenGLTextureSamplerManager(_extensions);
            _commandExecutor = new OpenGLCommandExecutor(this, platformInfo);

            int maxColorTextureSamples;
            if (_backendType == GraphicsBackend.OpenGL)
            {
                glGetIntegerv(GetPName.MaxColorTextureSamples, &maxColorTextureSamples);
                CheckLastError();
            }
            else
            {
                glGetIntegerv(GetPName.MaxSamples, &maxColorTextureSamples);
                CheckLastError();
            }
            if (maxColorTextureSamples >= 32)
            {
                _maxColorTextureSamples = TextureSampleCount.Count32;
            }
            else if (maxColorTextureSamples >= 16)
            {
                _maxColorTextureSamples = TextureSampleCount.Count16;
            }
            else if (maxColorTextureSamples >= 8)
            {
                _maxColorTextureSamples = TextureSampleCount.Count8;
            }
            else if (maxColorTextureSamples >= 4)
            {
                _maxColorTextureSamples = TextureSampleCount.Count4;
            }
            else if (maxColorTextureSamples >= 2)
            {
                _maxColorTextureSamples = TextureSampleCount.Count2;
            }
            else
            {
                _maxColorTextureSamples = TextureSampleCount.Count1;
            }

            int maxTexSize;

            glGetIntegerv(GetPName.MaxTextureSize, &maxTexSize);
            CheckLastError();

            int maxTexDepth;
            glGetIntegerv(GetPName.Max3DTextureSize, &maxTexDepth);
            CheckLastError();

            int maxTexArrayLayers;
            glGetIntegerv(GetPName.MaxArrayTextureLayers, &maxTexArrayLayers);
            CheckLastError();

            if (options.PreferDepthRangeZeroToOne && _extensions.ARB_ClipControl)
            {
                glClipControl(ClipControlOrigin.LowerLeft, ClipControlDepthRange.ZeroToOne);
                CheckLastError();
                _isDepthRangeZeroToOne = true;
            }

            _maxTextureSize = (uint)maxTexSize;
            _maxTexDepth = (uint)maxTexDepth;
            _maxTexArrayLayers = (uint)maxTexArrayLayers;

            _mainSwapchain = new OpenGLSwapchain(
                this,
                _swapchainFramebuffer,
                platformInfo.ResizeSwapchain);

            _workItems = new Queue<ExecutionThreadWorkItem>();
            platformInfo.ClearCurrentContext();
            _executionThread = new ExecutionThread(this, _workItems, _makeCurrent, _glContext);
            _openglInfo = new BackendInfoOpenGL(this);

            PostDeviceCreated();
        }

        private bool ManualSrgbBackbufferQuery()
        {
            if (_backendType == GraphicsBackend.OpenGLES && !_extensions.EXT_sRGBWriteControl)
            {
                return false;
            }

            uint copySrc;
            glGenTextures(1, &copySrc);
            CheckLastError();

            float* data = stackalloc float[4];
            data[0] = 0.5f;
            data[1] = 0.5f;
            data[2] = 0.5f;
            data[3] = 1f;

            glActiveTexture(TextureUnit.Texture0);
            CheckLastError();
            glBindTexture(TextureTarget.Texture2D, copySrc);
            CheckLastError();
            glTexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, GLPixelFormat.Rgba, GLPixelType.Float, data);
            CheckLastError();
            uint copySrcFb;
            glGenFramebuffers(1, &copySrcFb);
            CheckLastError();

            glBindFramebuffer(FramebufferTarget.ReadFramebuffer, copySrcFb);
            CheckLastError();
            glFramebufferTexture2D(FramebufferTarget.ReadFramebuffer, GLFramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, copySrc, 0);
            CheckLastError();

            glEnable(EnableCap.FramebufferSrgb);
            CheckLastError();
            glBlitFramebuffer(
                0, 0, 1, 1,
                0, 0, 1, 1,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            CheckLastError();

            glDisable(EnableCap.FramebufferSrgb);
            CheckLastError();

            glBindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            CheckLastError();
            glBindFramebuffer(FramebufferTarget.DrawFramebuffer, copySrcFb);
            CheckLastError();
            glBlitFramebuffer(
                0, 0, 1, 1,
                0, 0, 1, 1,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
            CheckLastError();
            if (_backendType == GraphicsBackend.OpenGLES)
            {
                glBindFramebuffer(FramebufferTarget.ReadFramebuffer, copySrc);
                CheckLastError();
                glReadPixels(
                    0, 0, 1, 1,
                    GLPixelFormat.Rgba,
                    GLPixelType.Float,
                    data);
                CheckLastError();
            }
            else
            {
                glGetTexImage(TextureTarget.Texture2D, 0, GLPixelFormat.Rgba, GLPixelType.Float, data);
                CheckLastError();
            }

            glDeleteFramebuffers(1, &copySrcFb);
            glDeleteTextures(1, &copySrc);

            return data[0] > 0.6f;
        }

        public OpenGLGraphicsDevice(GraphicsDeviceOptions options, SwapchainDescription swapchainDescription)
        {
            options.SwapchainDepthFormat = swapchainDescription.DepthFormat;
            options.SwapchainSrgbFormat = swapchainDescription.ColorSrgb;
            options.SyncToVerticalBlank = swapchainDescription.SyncToVerticalBlank;

            SwapchainSource source = swapchainDescription.Source;
            if (source is UIViewSwapchainSource uiViewSource)
            {
                InitializeUIView(options, uiViewSource.UIView);
            }
            else if (source is AndroidSurfaceSwapchainSource androidSource)
            {
                IntPtr aNativeWindow = Android.AndroidRuntime.ANativeWindow_fromSurface(
                    androidSource.JniEnv,
                    androidSource.Surface);
                InitializeANativeWindow(options, aNativeWindow, swapchainDescription);
            }
            else
            {
                throw new VeldridException(
                    "This function does not support creating an OpenGLES GraphicsDevice with the given SwapchainSource.");
            }
        }

        private void InitializeUIView(GraphicsDeviceOptions options, IntPtr uIViewPtr)
        {
            EAGLContext eaglContext = EAGLContext.Create(EAGLRenderingAPI.OpenGLES3);
            if (!EAGLContext.setCurrentContext(eaglContext.NativePtr))
            {
                throw new VeldridException("Unable to make newly-created EAGLContext current.");
            }

            MetalBindings.UIView uiView = new MetalBindings.UIView(uIViewPtr);

            CAEAGLLayer eaglLayer = CAEAGLLayer.New();
            eaglLayer.opaque = true;
            eaglLayer.frame = uiView.frame;
            uiView.layer.addSublayer(eaglLayer.NativePtr);

            NativeLibrary glesLibrary = new NativeLibrary("/System/Library/Frameworks/OpenGLES.framework/OpenGLES");

            Func<string, IntPtr> getProcAddress = name => glesLibrary.LoadFunction(name);

            LoadAllFunctions(eaglContext.NativePtr, getProcAddress, true);

            uint fb;
            glGenFramebuffers(1, &fb);
            CheckLastError();
            glBindFramebuffer(FramebufferTarget.Framebuffer, fb);
            CheckLastError();

            uint colorRB;
            glGenRenderbuffers(1, &colorRB);
            CheckLastError();

            glBindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRB);
            CheckLastError();

            bool result = eaglContext.renderBufferStorage((UIntPtr)RenderbufferTarget.Renderbuffer, eaglLayer.NativePtr);
            if (!result)
            {
                throw new VeldridException($"Failed to associate OpenGLES Renderbuffer with CAEAGLLayer.");
            }

            int fbWidth;
            glGetRenderbufferParameteriv(
                RenderbufferTarget.Renderbuffer,
                RenderbufferPname.RenderbufferWidth,
                &fbWidth);
            CheckLastError();

            int fbHeight;
            glGetRenderbufferParameteriv(
                RenderbufferTarget.Renderbuffer,
                RenderbufferPname.RenderbufferHeight,
                &fbHeight);
            CheckLastError();

            glFramebufferRenderbuffer(
                FramebufferTarget.Framebuffer,
                GLFramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer,
                colorRB);
            CheckLastError();

            uint depthRB = 0;
            bool hasDepth = options.SwapchainDepthFormat != null;
            if (hasDepth)
            {
                glGenRenderbuffers(1, &depthRB);
                CheckLastError();

                glBindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRB);
                CheckLastError();

                glRenderbufferStorage(
                    RenderbufferTarget.Renderbuffer,
                    (uint)OpenGLFormats.VdToGLSizedInternalFormat(options.SwapchainDepthFormat.Value, true),
                    (uint)fbWidth,
                    (uint)fbHeight);
                CheckLastError();

                glFramebufferRenderbuffer(
                    FramebufferTarget.Framebuffer,
                    GLFramebufferAttachment.DepthAttachment,
                    RenderbufferTarget.Renderbuffer,
                    depthRB);
                CheckLastError();
            }

            FramebufferErrorCode status = glCheckFramebufferStatus(FramebufferTarget.Framebuffer);
            CheckLastError();
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new VeldridException($"The OpenGLES main Swapchain Framebuffer was incomplete after initialization.");
            }

            glBindFramebuffer(FramebufferTarget.Framebuffer, fb);
            CheckLastError();

            Action<IntPtr> setCurrentContext = ctx =>
            {
                if (!EAGLContext.setCurrentContext(ctx))
                {
                    throw new VeldridException($"Unable to set the thread's current GL context.");
                }
            };

            uint colorRenderBuffer = colorRB;
            Action swapBuffers = () =>
            {
                glBindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRenderBuffer);
                CheckLastError();

                bool presentResult = eaglContext.presentRenderBuffer((UIntPtr)RenderbufferTarget.Renderbuffer);
                CheckLastError();
                if (!presentResult)
                {
                    throw new VeldridException($"Failed to present the EAGL RenderBuffer.");
                }
            };

            uint framebuffer = fb;
            Action setSwapchainFramebuffer = () =>
            {
                glBindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                CheckLastError();
            };

            uint depthRenderbuffer = depthRB;
            Action<uint, uint> resizeSwapchain = (w, h) =>
            {
                eaglLayer.frame = uiView.frame;

                _executionThread.Run(() =>
                {
                    glBindRenderbuffer(RenderbufferTarget.Renderbuffer, colorRenderBuffer);
                    CheckLastError();

                    bool rbStorageResult = eaglContext.renderBufferStorage(
                        (UIntPtr)RenderbufferTarget.Renderbuffer,
                        eaglLayer.NativePtr);
                    if (!rbStorageResult)
                    {
                        throw new VeldridException($"Failed to associate OpenGLES Renderbuffer with CAEAGLLayer.");
                    }

                    int newWidth;
                    glGetRenderbufferParameteriv(
                        RenderbufferTarget.Renderbuffer,
                        RenderbufferPname.RenderbufferWidth,
                        &newWidth);
                    CheckLastError();

                    int newHeight;
                    glGetRenderbufferParameteriv(
                        RenderbufferTarget.Renderbuffer,
                        RenderbufferPname.RenderbufferHeight,
                        &newHeight);
                    CheckLastError();

                    if (hasDepth)
                    {
                        Debug.Assert(depthRenderbuffer != 0);
                        glBindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRenderbuffer);
                        CheckLastError();

                        glRenderbufferStorage(
                            RenderbufferTarget.Renderbuffer,
                            (uint)OpenGLFormats.VdToGLSizedInternalFormat(options.SwapchainDepthFormat.Value, true),
                            (uint)newWidth,
                            (uint)newHeight);
                        CheckLastError();
                    }
                });
            };

            Action<IntPtr> destroyContext = ctx =>
            {
                eaglLayer.removeFromSuperlayer();
                eaglLayer.Release();
                eaglContext.Release();
                glesLibrary.Dispose();
            };

            OpenGLPlatformInfo platformInfo = new OpenGLPlatformInfo(
                eaglContext.NativePtr,
                getProcAddress,
                setCurrentContext,
                () => EAGLContext.currentContext.NativePtr,
                () => setCurrentContext(IntPtr.Zero),
                destroyContext,
                swapBuffers,
                syncInterval => { },
                setSwapchainFramebuffer,
                resizeSwapchain);

            Init(options, platformInfo, (uint)fbWidth, (uint)fbHeight, false);
        }

        private void InitializeANativeWindow(
            GraphicsDeviceOptions options,
            IntPtr aNativeWindow,
            SwapchainDescription swapchainDescription)
        {
            IntPtr display = eglGetDisplay(0);
            if (display == IntPtr.Zero)
            {
                throw new VeldridException($"Failed to get the default Android EGLDisplay: {eglGetError()}");
            }

            int major, minor;
            if (eglInitialize(display, &major, &minor) == 0)
            {
                throw new VeldridException($"Failed to initialize EGL: {eglGetError()}");
            }

            int* attribs = stackalloc int[]
            {
                EGL_RED_SIZE,
                8,
                EGL_GREEN_SIZE,
                8,
                EGL_BLUE_SIZE,
                8,
                EGL_ALPHA_SIZE,
                8,
                EGL_DEPTH_SIZE,
                swapchainDescription.DepthFormat != null
                    ? GetDepthBits(swapchainDescription.DepthFormat.Value)
                    : 0,
                EGL_SURFACE_TYPE,
                EGL_WINDOW_BIT,
                EGL_RENDERABLE_TYPE,
                EGL_OPENGL_ES3_BIT,
                EGL_NONE,
            };

            IntPtr* configs = stackalloc IntPtr[50];

            int num_config;
            if (eglChooseConfig(display, attribs, configs, 50, &num_config) == 0)
            {
                throw new VeldridException($"Failed to select a valid EGLConfig: {eglGetError()}");
            }

            IntPtr bestConfig = configs[0];

            int format;
            if (eglGetConfigAttrib(display, bestConfig, EGL_NATIVE_VISUAL_ID, &format) == 0)
            {
                throw new VeldridException($"Failed to get the EGLConfig's format: {eglGetError()}");
            }

            Android.AndroidRuntime.ANativeWindow_setBuffersGeometry(aNativeWindow, 0, 0, format);

            IntPtr eglWindowSurface = eglCreateWindowSurface(display, bestConfig, aNativeWindow, null);
            if (eglWindowSurface == IntPtr.Zero)
            {
                throw new VeldridException(
                    $"Failed to create an EGL surface from the Android native window: {eglGetError()}");
            }

            int* contextAttribs = stackalloc int[3];
            contextAttribs[0] = EGL_CONTEXT_CLIENT_VERSION;
            contextAttribs[1] = 2;
            contextAttribs[2] = EGL_NONE;
            IntPtr context = eglCreateContext(display, bestConfig, IntPtr.Zero, contextAttribs);
            if (context == IntPtr.Zero)
            {
                throw new VeldridException($"Failed to create an EGLContext: " + eglGetError());
            }

            Action<IntPtr> makeCurrent = ctx =>
            {
                if (eglMakeCurrent(display, eglWindowSurface, eglWindowSurface, ctx) == 0)
                {
                    throw new VeldridException($"Failed to make the EGLContext {ctx} current: {eglGetError()}");
                }
            };

            makeCurrent(context);

            Action clearContext = () =>
            {
                if (eglMakeCurrent(display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0)
                {
                    throw new VeldridException("Failed to clear the current EGLContext: " + eglGetError());
                }
            };

            Action swapBuffers = () =>
            {
                if (eglSwapBuffers(display, eglWindowSurface) == 0)
                {
                    throw new VeldridException("Failed to swap buffers: " + eglGetError());
                }
            };

            Action<bool> setSync = vsync =>
            {
                if (eglSwapInterval(display, vsync ? 1 : 0) == 0)
                {
                    throw new VeldridException($"Failed to set the swap interval: " + eglGetError());
                }
            };

            // Set the desired initial state.
            setSync(swapchainDescription.SyncToVerticalBlank);

            Action<IntPtr> destroyContext = ctx =>
            {
                if (eglDestroyContext(display, ctx) == 0)
                {
                    throw new VeldridException($"Failed to destroy EGLContext {ctx}: {eglGetError()}");
                }
            };

            OpenGLPlatformInfo platformInfo = new OpenGLPlatformInfo(
                context,
                eglGetProcAddress,
                makeCurrent,
                eglGetCurrentContext,
                clearContext,
                destroyContext,
                swapBuffers,
                setSync);

            Init(options, platformInfo, swapchainDescription.Width, swapchainDescription.Height, true);
        }

        private static int GetDepthBits(PixelFormat value)
        {
            switch (value)
            {
                case PixelFormat.R16_UNorm:
                    return 16;
                case PixelFormat.R32_Float:
                    return 32;
                default:
                    throw new VeldridException($"Unsupported depth format: {value}");
            }
        }

        private protected override void SubmitCommandsCore(
            CommandList cl,
            Fence fence)
        {
            lock (_commandListDisposalLock)
            {
                OpenGLCommandList glCommandList = Util.AssertSubtype<CommandList, OpenGLCommandList>(cl);
                OpenGLCommandEntryList entryList = glCommandList.CurrentCommands;
                IncrementCount(glCommandList);
                _executionThread.ExecuteCommands(entryList);
                if (fence is OpenGLFence glFence)
                {
                    glFence.Set();
                }
            }
        }

        private int IncrementCount(OpenGLCommandList glCommandList)
        {
            if (_submittedCommandListCounts.TryGetValue(glCommandList, out int count))
            {
                count += 1;
            }
            else
            {
                count = 1;
            }

            _submittedCommandListCounts[glCommandList] = count;
            return count;
        }

        private int DecrementCount(OpenGLCommandList glCommandList)
        {
            if (_submittedCommandListCounts.TryGetValue(glCommandList, out int count))
            {
                count -= 1;
            }
            else
            {
                count = -1;
            }

            if (count == 0)
            {
                _submittedCommandListCounts.Remove(glCommandList);
            }
            else
            {
                _submittedCommandListCounts[glCommandList] = count;
            }
            return count;
        }

        private int GetCount(OpenGLCommandList glCommandList)
        {
            return _submittedCommandListCounts.TryGetValue(glCommandList, out int count) ? count : 0;
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            WaitForIdle();

            _executionThread.SwapBuffers();
        }

        private protected override void WaitForIdleCore()
        {
            _executionThread.WaitForIdle();
        }

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
        {
            return _maxColorTextureSamples;
        }

        private protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            if (type == TextureType.Texture1D && !_features.Texture1D
                || !OpenGLFormats.IsFormatSupported(_extensions, format, _backendType))
            {
                properties = default;
                return false;
            }

            uint sampleCounts = 0;
            int max = (int)_maxColorTextureSamples + 1;
            for (int i = 0; i < max; i++)
            {
                sampleCounts |= (uint)(1 << i);
            }

            properties = new PixelFormatProperties(
                _maxTextureSize,
                type == TextureType.Texture1D ? 1 : _maxTextureSize,
                type != TextureType.Texture3D ? 1 : _maxTexDepth,
                uint.MaxValue,
                type == TextureType.Texture3D ? 1 : _maxTexArrayLayers,
                sampleCounts);
            return true;
        }

        private protected override MappedResource MapCore(
            MappableResource resource, uint offsetInBytes, uint sizeInBytes, MapMode mode, uint subresource)
        {
            return _executionThread.Map(resource, offsetInBytes, sizeInBytes, mode, subresource);
        }

        private protected override void UnmapCore(MappableResource resource, uint subresource)
        {
            _executionThread.Unmap(resource, subresource);
        }

        internal void CreateBuffer(DeviceBuffer buffer, IntPtr initialData)
        {
            _executionThread.CreateBuffer(buffer, initialData);
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            lock (_mappedResourceLock)
            {
                if (_mappedResources.ContainsKey(new MappedResourceCacheKey(buffer, 0)))
                {
                    throw new VeldridException("Cannot call UpdateBuffer on a currently-mapped Buffer.");
                }
            }
            StagingBlock sb = _stagingMemoryPool.Stage(source, sizeInBytes);
            _executionThread.UpdateBuffer(buffer, bufferOffsetInBytes, sb.Id);
        }

        private protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x,
            uint y,
            uint z,
            uint width,
            uint height,
            uint depth,
            uint mipLevel,
            uint arrayLayer)
        {
            StagingBlock textureData = _stagingMemoryPool.Stage(source, sizeInBytes);
            StagingBlock argBlock = _stagingMemoryPool.GetStagingBlock(UpdateTextureArgsSize);
            ref UpdateTextureArgs args = ref Unsafe.AsRef<UpdateTextureArgs>(argBlock.Data);
            args.Data = (IntPtr)textureData.Data;
            args.X = x;
            args.Y = y;
            args.Z = z;
            args.Width = width;
            args.Height = height;
            args.Depth = depth;
            args.MipLevel = mipLevel;
            args.ArrayLayer = arrayLayer;

            _executionThread.UpdateTexture(texture, argBlock.Id, textureData.Id);
        }

        private static readonly uint UpdateTextureArgsSize = (uint)Unsafe.SizeOf<UpdateTextureArgs>();

        private struct UpdateTextureArgs
        {
            public IntPtr Data;
            public uint X;
            public uint Y;
            public uint Z;
            public uint Width;
            public uint Height;
            public uint Depth;
            public uint MipLevel;
            public uint ArrayLayer;
        }

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
        {
            return Util.AssertSubtype<Fence, OpenGLFence>(fence).Wait(nanosecondTimeout);
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            int msTimeout;
            if (nanosecondTimeout == ulong.MaxValue)
            {
                msTimeout = -1;
            }
            else
            {
                msTimeout = (int)Math.Min(nanosecondTimeout / 1_000_000, int.MaxValue);
            }

            ManualResetEvent[] events = GetResetEventArray(fences.Length);
            for (int i = 0; i < fences.Length; i++)
            {
                events[i] = Util.AssertSubtype<Fence, OpenGLFence>(fences[i]).ResetEvent;
            }
            bool result;
            if (waitAll)
            {
                result = WaitHandle.WaitAll(events, msTimeout);
            }
            else
            {
                int index = WaitHandle.WaitAny(events, msTimeout);
                result = index != WaitHandle.WaitTimeout;
            }

            ReturnResetEventArray(events);

            return result;
        }

        private ManualResetEvent[] GetResetEventArray(int length)
        {
            lock (_resetEventsLock)
            {
                for (int i = _resetEvents.Count - 1; i > 0; i--)
                {
                    ManualResetEvent[] array = _resetEvents[i];
                    if (array.Length == length)
                    {
                        _resetEvents.RemoveAt(i);
                        return array;
                    }
                }
            }

            ManualResetEvent[] newArray = new ManualResetEvent[length];
            return newArray;
        }

        private void ReturnResetEventArray(ManualResetEvent[] array)
        {
            lock (_resetEventsLock)
            {
                _resetEvents.Add(array);
            }
        }

        public override void ResetFence(Fence fence)
        {
            Util.AssertSubtype<Fence, OpenGLFence>(fence).Reset();
        }

        internal void EnqueueDisposal(OpenGLDeferredResource resource)
        {
            _resourcesToDispose.Enqueue(resource);
        }

        internal void EnqueueDisposal(OpenGLCommandList commandList)
        {
            lock (_commandListDisposalLock)
            {
                if (GetCount(commandList) > 0)
                {
                    _commandListsToDispose.Add(commandList);
                }
                else
                {
                    commandList.DestroyResources();
                }
            }
        }

        internal bool CheckCommandListDisposal(OpenGLCommandList commandList)
        {

            lock (_commandListDisposalLock)
            {
                int count = DecrementCount(commandList);
                if (count == 0)
                {
                    if (_commandListsToDispose.Remove(commandList))
                    {
                        commandList.DestroyResources();
                        return true;
                    }
                }

                return false;
            }
        }

        private void FlushDisposables()
        {
            while (_resourcesToDispose.TryDequeue(out OpenGLDeferredResource resource))
            {
                resource.DestroyGLResources();
            }
        }

        public void EnableDebugCallback() => EnableDebugCallback(DebugSeverity.DebugSeverityNotification);
        public void EnableDebugCallback(DebugSeverity minimumSeverity) => EnableDebugCallback(DefaultDebugCallback(minimumSeverity));
        public void EnableDebugCallback(DebugProc callback)
        {
            glEnable(EnableCap.DebugOutput);
            CheckLastError();
            // The debug callback delegate must be persisted, otherwise errors will occur
            // when the OpenGL drivers attempt to call it after it has been collected.
            _debugMessageCallback = callback;
            glDebugMessageCallback(_debugMessageCallback, null);
            CheckLastError();
        }

        private DebugProc DefaultDebugCallback(DebugSeverity minimumSeverity)
        {
            return (source, type, id, severity, length, message, userParam) =>
            {
                if (severity >= minimumSeverity
                    && type != DebugType.DebugTypeMarker
                    && type != DebugType.DebugTypePushGroup
                    && type != DebugType.DebugTypePopGroup)
                {
                    string messageString = Marshal.PtrToStringAnsi((IntPtr)message, (int)length);
                    Debug.WriteLine($"GL DEBUG MESSAGE: {source}, {type}, {id}. {severity}: {messageString}");
                }
            };
        }

        protected override void PlatformDispose()
        {
            FlushAndFinish();
            _executionThread.Terminate();
        }

        public override bool GetOpenGLInfo(out BackendInfoOpenGL info)
        {
            info = _openglInfo;
            return true;
        }

        internal void ExecuteOnGLThread(Action action)
        {
            _executionThread.Run(action);
            _executionThread.WaitForIdle();
        }

        internal void FlushAndFinish()
        {
            _executionThread.FlushAndFinish();
        }

        internal void EnsureResourceInitialized(OpenGLDeferredResource deferredResource)
        {
            _executionThread.InitializeResource(deferredResource);
        }

        internal override uint GetUniformBufferMinOffsetAlignmentCore() => _minUboOffsetAlignment;

        internal override uint GetStructuredBufferMinOffsetAlignmentCore() => _minSsboOffsetAlignment;

        private class ExecutionThread
        {
            private readonly OpenGLGraphicsDevice _gd;
            private readonly Queue<ExecutionThreadWorkItem> _workItems;
            private readonly Action<IntPtr> _makeCurrent;
            private readonly IntPtr _context;
            private bool _terminated;
            private readonly List<Exception> _exceptions = new List<Exception>();
            private readonly object _exceptionsLock = new object();

            public ExecutionThread(
                OpenGLGraphicsDevice gd,
                Queue<ExecutionThreadWorkItem> workItems,
                Action<IntPtr> makeCurrent,
                IntPtr context)
            {
                _gd = gd;
                _workItems = workItems;
                _makeCurrent = makeCurrent;
                _context = context;
                Thread thread = new Thread(Run);
                thread.IsBackground = true;
                thread.Start();
            }

            private void Run()
            {
                _makeCurrent(_context);
                while (!_terminated)
                {
                    bool hasItem;
                    ExecutionThreadWorkItem workItem;
                    lock (_workItems)
                    {
                        hasItem = _workItems.TryDequeue(out workItem);
                    }

                    if (hasItem)
                    {
                        ExecuteWorkItem(ref workItem);
                    }
                }
            }

            private void ExecuteWorkItem(ref ExecutionThreadWorkItem workItem)
            {
                try
                {
                    switch (workItem.Type)
                    {
                        case WorkItemType.ExecuteList:
                        {
                            OpenGLCommandEntryList list = Unsafe.As<OpenGLCommandEntryList>(workItem.Object0);
                            try
                            {
                                list.ExecuteAll(_gd._commandExecutor);
                            }
                            finally
                            {
                                if (!_gd.CheckCommandListDisposal(list.Parent))
                                {
                                    list.Parent.OnCompleted(list);
                                }
                            }
                        }
                        break;

                        case WorkItemType.Map:
                        {
                            MappableResource resourceToMap = Unsafe.As<MappableResource>(workItem.Object0);
                            ManualResetEventSlim mre = Unsafe.As<ManualResetEventSlim>(workItem.Object1);

                            MapParams* resultPtr = (MapParams*)Util.UnpackIntPtr(workItem.UInt0, workItem.UInt1);

                            ExecuteMapResource(
                                resourceToMap,
                                mre,
                                resultPtr);
                        }
                        break;

                        case WorkItemType.Unmap:
                        {
                            MappableResource resourceToMap = Unsafe.As<MappableResource>(workItem.Object0);
                            uint subresource = workItem.UInt0;

                            ExecuteUnmapResource(resourceToMap, subresource);
                        }
                        break;

                        case WorkItemType.UpdateBuffer:
                        {
                            DeviceBuffer updateBuffer = Unsafe.As<DeviceBuffer>(workItem.Object0);
                            uint offsetInBytes = workItem.UInt0;
                            StagingBlock stagingBlock = _gd.StagingMemoryPool.RetrieveById(workItem.UInt1);

                            _gd._commandExecutor.UpdateBuffer(
                                updateBuffer,
                                offsetInBytes,
                                (IntPtr)stagingBlock.Data,
                                stagingBlock.SizeInBytes);

                            _gd.StagingMemoryPool.Free(stagingBlock);
                        }
                        break;

                        case WorkItemType.CreateBuffer:
                        {
                            DeviceBuffer updateBuffer = Unsafe.As<DeviceBuffer>(workItem.Object0);
                            ManualResetEventSlim mre = Unsafe.As<ManualResetEventSlim>(workItem.Object1);
                            IntPtr resultPtr = Util.UnpackIntPtr(workItem.UInt0, workItem.UInt1);

                            OpenGLBuffer glBuffer = Util.AssertSubtype<DeviceBuffer, OpenGLBuffer>(updateBuffer);
                            glBuffer.CreateGLResources(resultPtr);
                        }
                        break;

                        case WorkItemType.UpdateTexture:
                            Texture texture = Unsafe.As<Texture>(workItem.Object0);
                            StagingMemoryPool pool = _gd.StagingMemoryPool;
                            StagingBlock argBlock = pool.RetrieveById(workItem.UInt0);
                            StagingBlock textureData = pool.RetrieveById(workItem.UInt1);
                            ref UpdateTextureArgs args = ref Unsafe.AsRef<UpdateTextureArgs>(argBlock.Data);

                            _gd._commandExecutor.UpdateTexture(
                                texture, args.Data, args.X, args.Y, args.Z,
                                args.Width, args.Height, args.Depth, args.MipLevel, args.ArrayLayer);

                            pool.Free(argBlock);
                            pool.Free(textureData);
                            break;

                        case WorkItemType.GenericAction:
                        {
                            Unsafe.As<Action>(workItem.Object0).Invoke();
                        }
                        break;

                        case WorkItemType.TerminateAction:
                        {
                            // Check if the OpenGL context has already been destroyed by the OS. If so, just exit out.
                            uint error = glGetError();
                            if (error != (uint)ErrorCode.InvalidOperation)
                            {
                                _makeCurrent(_gd._glContext);

                                _gd.FlushDisposables();
                                _gd._deleteContext(_gd._glContext);
                            }
                            _gd.StagingMemoryPool.Dispose();
                            _terminated = true;
                        }
                        break;

                        case WorkItemType.SetSyncToVerticalBlank:
                        {
                            bool value = workItem.UInt0 == 1 ? true : false;
                            _gd._setSyncToVBlank(value);
                        }
                        break;

                        case WorkItemType.SwapBuffers:
                        {
                            _gd._swapBuffers();
                            _gd.FlushDisposables();
                        }
                        break;

                        case WorkItemType.WaitForIdle:
                        {
                            _gd.FlushDisposables();
                            bool isFullFlush = workItem.UInt0 != 0;
                            if (isFullFlush)
                            {
                                glFlush();
                                glFinish();
                            }
                            Unsafe.As<ManualResetEventSlim>(workItem.Object0).Set();
                        }
                        break;

                        case WorkItemType.InitializeResource:
                        {
                            InitializeResourceInfo info = Unsafe.As<InitializeResourceInfo>(workItem.Object0);
                            try
                            {
                                info.DeferredResource.EnsureResourcesCreated();
                            }
                            catch (Exception e)
                            {
                                info.Exception = e;
                            }
                            finally
                            {
                                info.ResetEvent.Set();
                            }
                        }
                        break;

                        default:
                            throw new InvalidOperationException("Invalid command type: " + workItem.Type);
                    }
                }
                catch (Exception e) when (!Debugger.IsAttached)
                {
                    lock (_exceptionsLock)
                    {
                        _exceptions.Add(e);
                    }
                }
            }

            private void ExecuteMapResource(
                MappableResource resource,
                ManualResetEventSlim mre,
                MapParams* result)
            {
                uint subresource = result->Subresource;
                MapMode mode = result->MapMode;

                MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
                try
                {
                    lock (_gd._mappedResourceLock)
                    {
                        if (_gd._mappedResources.ContainsKey(key))
                        {
                            throw new VeldridException("The given resource is already mapped.");
                        }

                        MappedResourceInfo info = new MappedResourceInfo();

                        if (resource is OpenGLBuffer buffer)
                        {
                            buffer.EnsureResourcesCreated();
                            void* mappedPtr;
                            BufferAccessMask accessMask = OpenGLFormats.VdToGLMapMode(mode);
                            if (_gd.Extensions.ARB_DirectStateAccess)
                            {
                                mappedPtr = glMapNamedBufferRange(
                                    buffer.Buffer, (IntPtr)result->OffsetInBytes, result->SizeInBytes, accessMask);
                                CheckLastError();
                            }
                            else
                            {
                                glBindBuffer(BufferTarget.CopyWriteBuffer, buffer.Buffer);
                                CheckLastError();

                                mappedPtr = glMapBufferRange(
                                    BufferTarget.CopyWriteBuffer, (IntPtr)result->OffsetInBytes, (IntPtr)result->SizeInBytes, accessMask);
                                CheckLastError();
                            }

                            info.MappedResource = new MappedResource(
                                resource,
                                mode,
                                (IntPtr)mappedPtr,
                                result->OffsetInBytes,
                                result->SizeInBytes);

                            result->Data = (IntPtr)mappedPtr;
                            result->RowPitch = 0;
                            result->DepthPitch = 0;
                            result->Succeeded = true;
                        }
                        else
                        {
                            OpenGLTexture texture = Util.AssertSubtype<MappableResource, OpenGLTexture>(resource);
                            texture.EnsureResourcesCreated();

                            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                            Util.GetMipDimensions(texture, mipLevel, out uint mipWidth, out uint mipHeight, out uint mipDepth);

                            uint depthSliceSize = FormatHelpers.GetDepthPitch(
                                FormatHelpers.GetRowPitch(mipWidth, texture.Format),
                                mipHeight,
                                texture.Format);
                            uint subresourceSize = depthSliceSize * mipDepth;
                            int compressedSize = 0;

                            bool isCompressed = FormatHelpers.IsCompressedFormat(texture.Format);
                            if (isCompressed)
                            {
                                glGetTexLevelParameteriv(
                                    texture.TextureTarget,
                                    (int)mipLevel,
                                    GetTextureParameter.TextureCompressedImageSize,
                                    &compressedSize);
                                CheckLastError();
                            }

                            StagingBlock block = _gd._stagingMemoryPool.GetStagingBlock(subresourceSize);

                            uint packAlignment = 4;
                            if (!isCompressed)
                            {
                                packAlignment = FormatHelpers.GetSizeInBytes(texture.Format);
                            }

                            if (packAlignment < 4)
                            {
                                glPixelStorei(PixelStoreParameter.PackAlignment, (int)packAlignment);
                                CheckLastError();
                            }

                            if (mode == MapMode.Read || mode == MapMode.ReadWrite)
                            {
                                if (!isCompressed)
                                {
                                    // Read data into buffer.
                                    if (_gd.Extensions.ARB_DirectStateAccess)
                                    {
                                        int zoffset = texture.ArrayLayers > 1 ? (int)arrayLayer : 0;
                                        glGetTextureSubImage(
                                            texture.Texture,
                                            (int)mipLevel,
                                            0, 0, zoffset,
                                            mipWidth, mipHeight, mipDepth,
                                            texture.GLPixelFormat,
                                            texture.GLPixelType,
                                            subresourceSize,
                                            block.Data);
                                        CheckLastError();
                                    }
                                    else
                                    {
                                        for (uint layer = 0; layer < mipDepth; layer++)
                                        {
                                            uint curLayer = arrayLayer + layer;
                                            uint curOffset = depthSliceSize * layer;
                                            uint readFB;
                                            glGenFramebuffers(1, &readFB);
                                            CheckLastError();
                                            glBindFramebuffer(FramebufferTarget.ReadFramebuffer, readFB);
                                            CheckLastError();

                                            if (texture.ArrayLayers > 1 || texture.Type == TextureType.Texture3D)
                                            {
                                                glFramebufferTextureLayer(
                                                    FramebufferTarget.ReadFramebuffer,
                                                    GLFramebufferAttachment.ColorAttachment0,
                                                    texture.Texture,
                                                    (int)mipLevel,
                                                    (int)curLayer);
                                                CheckLastError();
                                            }
                                            else if (texture.Type == TextureType.Texture1D)
                                            {
                                                glFramebufferTexture1D(
                                                    FramebufferTarget.ReadFramebuffer,
                                                    GLFramebufferAttachment.ColorAttachment0,
                                                    TextureTarget.Texture1D,
                                                    texture.Texture,
                                                    (int)mipLevel);
                                                CheckLastError();
                                            }
                                            else
                                            {
                                                glFramebufferTexture2D(
                                                    FramebufferTarget.ReadFramebuffer,
                                                    GLFramebufferAttachment.ColorAttachment0,
                                                    TextureTarget.Texture2D,
                                                    texture.Texture,
                                                    (int)mipLevel);
                                                CheckLastError();
                                            }

                                            glReadPixels(
                                                0, 0,
                                                mipWidth, mipHeight,
                                                texture.GLPixelFormat,
                                                texture.GLPixelType,
                                                (byte*)block.Data + curOffset);
                                            CheckLastError();
                                            glDeleteFramebuffers(1, &readFB);
                                            CheckLastError();
                                        }
                                    }
                                }
                                else // isCompressed
                                {
                                    if (texture.TextureTarget == TextureTarget.Texture2DArray
                                        || texture.TextureTarget == TextureTarget.Texture2DMultisampleArray
                                        || texture.TextureTarget == TextureTarget.TextureCubeMapArray)
                                    {
                                        // We only want a single subresource (array slice), so we need to copy
                                        // a subsection of the downloaded data into our staging block.

                                        uint fullDataSize = (uint)compressedSize;
                                        StagingBlock fullBlock = _gd._stagingMemoryPool.GetStagingBlock(fullDataSize);

                                        if (_gd.Extensions.ARB_DirectStateAccess)
                                        {
                                            glGetCompressedTextureImage(
                                                texture.Texture,
                                                (int)mipLevel,
                                                fullBlock.SizeInBytes,
                                                fullBlock.Data);
                                            CheckLastError();
                                        }
                                        else
                                        {
                                            _gd.TextureSamplerManager.SetTextureTransient(texture.TextureTarget, texture.Texture);
                                            CheckLastError();

                                            glGetCompressedTexImage(texture.TextureTarget, (int)mipLevel, fullBlock.Data);
                                            CheckLastError();
                                        }
                                        byte* sliceStart = (byte*)fullBlock.Data + (arrayLayer * subresourceSize) + result->OffsetInBytes;
                                        Buffer.MemoryCopy(sliceStart, block.Data, subresourceSize, result->SizeInBytes);
                                        _gd._stagingMemoryPool.Free(fullBlock);
                                    }
                                    else
                                    {
                                        if (_gd.Extensions.ARB_DirectStateAccess)
                                        {
                                            glGetCompressedTextureImage(
                                                texture.Texture,
                                                (int)mipLevel,
                                                block.SizeInBytes,
                                                block.Data);
                                            CheckLastError();
                                        }
                                        else
                                        {
                                            _gd.TextureSamplerManager.SetTextureTransient(texture.TextureTarget, texture.Texture);
                                            CheckLastError();

                                            glGetCompressedTexImage(texture.TextureTarget, (int)mipLevel, block.Data);
                                            CheckLastError();
                                        }
                                    }
                                }
                            }

                            if (packAlignment < 4)
                            {
                                glPixelStorei(PixelStoreParameter.PackAlignment, 4);
                                CheckLastError();
                            }

                            uint rowPitch = FormatHelpers.GetRowPitch(mipWidth, texture.Format);
                            uint depthPitch = FormatHelpers.GetDepthPitch(rowPitch, mipHeight, texture.Format);

                            info.MappedResource = new MappedResource(
                                resource,
                                mode,
                                (IntPtr)block.Data,
                                result->OffsetInBytes,
                                result->SizeInBytes,
                                subresource,
                                rowPitch,
                                depthPitch);
                            info.StagingBlock = block;

                            result->Data = (IntPtr)block.Data;
                            result->RowPitch = rowPitch;
                            result->DepthPitch = depthPitch;
                            result->Succeeded = true;
                        }

                        _gd._mappedResources.Add(key, info);
                    }
                }
                catch
                {
                    result->Succeeded = false;
                    throw;
                }
                finally
                {
                    mre.Set();
                }
            }

            private void ExecuteUnmapResource(MappableResource resource, uint subresource)
            {
                MappedResourceCacheKey key = new MappedResourceCacheKey(resource, subresource);
                lock (_gd._mappedResourceLock)
                {
                    if (!_gd._mappedResources.Remove(key, out MappedResourceInfo info))
                    {
                        throw new VeldridException($"The given resource ({resource}) is not mapped.");
                    }

                    if (resource is OpenGLBuffer buffer)
                    {
                        if (_gd.Extensions.ARB_DirectStateAccess)
                        {
                            glUnmapNamedBuffer(buffer.Buffer);
                            CheckLastError();
                        }
                        else
                        {
                            glBindBuffer(BufferTarget.CopyWriteBuffer, buffer.Buffer);
                            CheckLastError();

                            glUnmapBuffer(BufferTarget.CopyWriteBuffer);
                            CheckLastError();
                        }
                    }
                    else
                    {
                        OpenGLTexture texture = Util.AssertSubtype<MappableResource, OpenGLTexture>(resource);

                        if (info.MappedResource.Mode == MapMode.Write ||
                            info.MappedResource.Mode == MapMode.ReadWrite)
                        {
                            Util.GetMipLevelAndArrayLayer(texture, subresource, out uint mipLevel, out uint arrayLayer);
                            Util.GetMipDimensions(texture, mipLevel, out uint width, out uint height, out uint depth);

                            IntPtr data = (IntPtr)info.StagingBlock.Data;

                            _gd._commandExecutor.UpdateTexture(
                                texture,
                                data,
                                0, 0, 0,
                                width, height, depth,
                                mipLevel,
                                arrayLayer);
                        }

                        _gd.StagingMemoryPool.Free(info.StagingBlock);
                    }
                }
            }

            private void CheckExceptions()
            {
                lock (_exceptionsLock)
                {
                    if (_exceptions.Count > 0)
                    {
                        Exception innerException = _exceptions.Count == 1
                            ? _exceptions[0]
                            : new AggregateException(_exceptions.ToArray());
                        _exceptions.Clear();
                        throw new VeldridException(
                            "Error(s) were encountered during the execution of OpenGL commands. See InnerException for more information.",
                            innerException);

                    }
                }
            }

            public void CreateBuffer(DeviceBuffer buffer, IntPtr source)
            {
                CheckExceptions();

                ManualResetEventSlim mre = new ManualResetEventSlim();

                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(buffer, mre, source);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
                mre.Wait();
                mre.Dispose();
            }

            public MappedResource Map(
                MappableResource resource, uint offsetInBytes, uint sizeInBytes, MapMode mode, uint subresource)
            {
                CheckExceptions();

                MapParams mrp = new MapParams();
                mrp.OffsetInBytes = offsetInBytes;
                mrp.SizeInBytes = sizeInBytes;
                mrp.Subresource = subresource;
                mrp.MapMode = mode;

                ManualResetEventSlim mre = new ManualResetEventSlim(false);
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(resource, &mrp, mre);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
                mre.Wait();

                if (!mrp.Succeeded)
                {
                    throw new VeldridException("Failed to map OpenGL resource.");
                }

                mre.Dispose();

                return new MappedResource(
                    resource, mode, mrp.Data, mrp.OffsetInBytes, mrp.SizeInBytes, mrp.Subresource, mrp.RowPitch, mrp.DepthPitch);
            }

            internal void Unmap(MappableResource resource, uint subresource)
            {
                CheckExceptions();

                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(resource, subresource);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            public void ExecuteCommands(OpenGLCommandEntryList entryList)
            {
                CheckExceptions();

                entryList.Parent.OnSubmitted(entryList);
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(entryList);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void UpdateBuffer(DeviceBuffer buffer, uint offsetInBytes, uint dataBlockId)
            {
                CheckExceptions();

                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(buffer, offsetInBytes, dataBlockId);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void UpdateTexture(Texture texture, uint argBlockId, uint dataBlockId)
            {
                CheckExceptions();

                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(texture, argBlockId, dataBlockId);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void Run(Action a)
            {
                CheckExceptions();

                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(a);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void Terminate()
            {
                CheckExceptions();

                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(WorkItemType.TerminateAction);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void WaitForIdle()
            {
                ManualResetEventSlim mre = new ManualResetEventSlim();
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(mre, isFullFlush: false);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
                mre.Wait();
                mre.Dispose();

                CheckExceptions();
            }

            internal void SetSyncToVerticalBlank(bool value)
            {
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(value);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void SwapBuffers()
            {
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(WorkItemType.SwapBuffers);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
            }

            internal void FlushAndFinish()
            {
                ManualResetEventSlim mre = new ManualResetEventSlim();
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(mre, isFullFlush: true);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
                mre.Wait();
                mre.Dispose();

                CheckExceptions();
            }

            internal void InitializeResource(OpenGLDeferredResource deferredResource)
            {
                InitializeResourceInfo info = new InitializeResourceInfo(deferredResource, new ManualResetEventSlim());
                ExecutionThreadWorkItem workItem = new ExecutionThreadWorkItem(info);
                lock (_workItems)
                {
                    _workItems.Enqueue(workItem);
                }
                info.ResetEvent.Wait();
                info.ResetEvent.Dispose();

                if (info.Exception != null)
                {
                    throw info.Exception;
                }
            }
        }

        public enum WorkItemType : byte
        {
            Map,
            Unmap,
            ExecuteList,
            UpdateBuffer,
            CreateBuffer,
            UpdateTexture,
            GenericAction,
            TerminateAction,
            SetSyncToVerticalBlank,
            SwapBuffers,
            WaitForIdle,
            InitializeResource,
        }

        private unsafe struct ExecutionThreadWorkItem
        {
            public readonly WorkItemType Type;
            public readonly object Object0;
            public readonly object Object1;
            public readonly uint UInt0;
            public readonly uint UInt1;
            public readonly uint UInt2;

            public ExecutionThreadWorkItem(
                MappableResource resource,
                MapParams* mapResult,
                ManualResetEventSlim resetEvent)
            {
                Type = WorkItemType.Map;
                Object0 = resource;
                Object1 = resetEvent;

                Util.PackIntPtr((IntPtr)mapResult, out UInt0, out UInt1);
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(MappableResource resource, uint subresource)
            {
                Type = WorkItemType.Unmap;
                Object0 = resource;
                Object1 = null;

                UInt0 = subresource;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(OpenGLCommandEntryList commandList)
            {
                Type = WorkItemType.ExecuteList;
                Object0 = commandList;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(DeviceBuffer updateBuffer, uint offsetInBytes, uint dataBlockId)
            {
                Type = WorkItemType.UpdateBuffer;
                Object0 = updateBuffer;
                Object1 = null;

                UInt0 = offsetInBytes;
                UInt1 = dataBlockId;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(DeviceBuffer createBuffer, ManualResetEventSlim mre, IntPtr initialData)
            {
                Type = WorkItemType.CreateBuffer;
                Object0 = createBuffer;
                Object1 = mre;

                Util.PackIntPtr(initialData, out UInt0, out UInt1);
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(Action a, bool isTermination = false)
            {
                Type = isTermination ? WorkItemType.TerminateAction : WorkItemType.GenericAction;
                Object0 = a;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(Texture texture, uint argBlockId, uint dataBlockId)
            {
                Type = WorkItemType.UpdateTexture;
                Object0 = texture;
                Object1 = null;

                UInt0 = argBlockId;
                UInt1 = dataBlockId;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(ManualResetEventSlim mre, bool isFullFlush)
            {
                Type = WorkItemType.WaitForIdle;
                Object0 = mre;
                Object1 = null;

                UInt0 = isFullFlush ? 1u : 0u;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(bool value)
            {
                Type = WorkItemType.SetSyncToVerticalBlank;
                Object0 = null;
                Object1 = null;

                UInt0 = value ? 1u : 0u;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(WorkItemType type)
            {
                Type = type;
                Object0 = null;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }

            public ExecutionThreadWorkItem(InitializeResourceInfo info)
            {
                Type = WorkItemType.InitializeResource;
                Object0 = info;
                Object1 = null;

                UInt0 = 0;
                UInt1 = 0;
                UInt2 = 0;
            }
        }

        private struct MapParams
        {
            public MapMode MapMode;
            public uint OffsetInBytes;
            public uint SizeInBytes;
            public uint Subresource;
            public bool Succeeded;
            public IntPtr Data;
            public uint RowPitch;
            public uint DepthPitch;
        }

        internal struct MappedResourceInfo
        {
            public MappedResource MappedResource;
            public StagingBlock StagingBlock;
        }

        private class InitializeResourceInfo
        {
            public OpenGLDeferredResource DeferredResource;
            public ManualResetEventSlim ResetEvent;
            public Exception Exception;

            public InitializeResourceInfo(OpenGLDeferredResource deferredResource, ManualResetEventSlim mre)
            {
                DeferredResource = deferredResource;
                ResetEvent = mre;
            }
        }
    }
}
