using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Core.Platform;
using OpenTK.Platform.Native;
using OpenTK.Core.Utility;

namespace OpenTK.Backends.Tests
{
    [JsonSerializable(typeof(BackendsConfig))]
    public class BackendsConfig
    {
        // Because this tool not only aims to support third party developers
        // making their own platform drivers, it should support a multitude of
        // options for dependency injection.
        //
        // 1. The assemblies in LoadExtraAssemblies are loaded.
        // 2. If any specific driver name is provided, these are created using reflection.
        // 3. Any driver names not provided are loaded using normal driver loading logic.
        //      - PreferSDL2 is passed to the OpenTK.Platform.Native loader.


        /// <summary>
        /// Provides the preferred backend to pick.
        /// </summary>
        /// <remarks>Use "auto" for automatic.</remarks>
        /// <seealso cref="OpenTK.Platform.Native.Backend"/>
        public bool PreferSDL2 {get; set; } = false;

        public List<string> ExtraAssemblies { get; set; } = new List<string>();

        // These keys provide overrides for specific backends.
        public string? OpenGL { get; set; }
        public string? Vulkan { get; set; }
        public string? WindowIcon { get; set; }
        public string? MouseCursor { get; set; }
        public string? Window { get; set; }
        public string? Surface { get; set; }
        public string? Display { get; set; }
        public string? MiceInput { get; set; }
        public string? KeyboardInput { get; set; }
        public string? ControllerInput { get; set; }
        public string? Clipboard { get; set; }
        public string? Shell { get; set; }
        public string? Joystick { get; set; }

        [JsonIgnore]
        public string? this[PalComponents component] => component switch
        {
            PalComponents.OpenGL => OpenGL,
            PalComponents.Vulkan => Vulkan,
            PalComponents.WindowIcon => WindowIcon,
            PalComponents.MouseCursor => MouseCursor,
            PalComponents.Window => Window,
            PalComponents.Surface => Surface,
            PalComponents.Display => Display,
            PalComponents.MiceInput => MiceInput,
            PalComponents.KeyboardInput => KeyboardInput,
            PalComponents.ControllerInput => ControllerInput,
            PalComponents.Clipboard => Clipboard,
            PalComponents.Shell => Shell,
            PalComponents.Joystick => Joystick,
            _ => null
        };

        public BackendsConfig() { }

        private static BackendsConfig? _singleton = null;

        public static ILogger? Logger = null;

        /// <summary>
        /// The configuration singleton.
        /// </summary>
        public static BackendsConfig Singleton
        {
            get
            {
                if (_singleton != null)
                    return _singleton;

                LoadSingleton();

                return _singleton!;
            } 
        }

        /// <summary>
        /// Indicates if any extra assemblies were loaded.
        /// </summary>
        public static bool ExtraAssembliesLoaded { get; private set; }

        static void LoadSingleton()
        {
            string[] argv = Environment.GetCommandLineArgs();

            BackendsConfig? config = null;
            try
            {
                if (argv.Length > 1)
                {
                    string pathOrConfig = argv[1];

                    // Allow comments in JSON.
                    JsonSerializerOptions options = new JsonSerializerOptions()
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                    };

                    // If the argument is a file, load the file, else try to interpret as JSON.

                    try
                    {
                        if (File.Exists(pathOrConfig))
                        {
                            using Stream stream = File.Open(pathOrConfig, FileMode.Open);
                            config = JsonSerializer.Deserialize<BackendsConfig>(stream, options);
                        }
                        else
                        {
                            config = JsonSerializer.Deserialize<BackendsConfig>(pathOrConfig, options);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"Could not load configuration: {ex}\n{ex.StackTrace}");

                        if (Debugger.IsAttached)
                            Debugger.Break();
                    }
                }

                // Make sure there actually is a backend config.
                if (config == null)
                    config = new BackendsConfig();

                if (config.ExtraAssemblies?.Count > 0)
                {
                    foreach (string name in config.ExtraAssemblies)
                    {
                        try
                        {
                            Assembly.Load(name);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogError($"Could not load assembly \"{name}\": {ex}\n{ex.StackTrace}");

                            if (Debugger.IsAttached)
                                Debugger.Break();
                        }
                    }

                    ExtraAssembliesLoaded = true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError($"An unkown error occured during loading the configuration: {ex}\n{ex.StackTrace}");

                if (Debugger.IsAttached)
                    Debugger.Break();
            }
            finally
            {
                _singleton = config ?? new BackendsConfig();
            }
        }

        public static IPalComponent? GetBackend(PalComponents component)
        {
            IPalComponent? driver = null;
            string? overridePath = Singleton[component];

            if (overridePath != null)
            {
                try
                {
                    Type type = Type.GetType(overridePath)
                                ?? throw new Exception($"Could not find type {overridePath}");

                    ConstructorInfo ctor = type?.GetConstructor(BindingFlags.Default, Array.Empty<Type>())
                                            ?? throw new Exception("No suitable constructor found.");

                    driver = (IPalComponent)ctor.Invoke(Array.Empty<object>());
                }
                catch (Exception ex)
                {
                    Logger?.LogError($"Could not load overridden platform driver \"{overridePath}\": {ex}\n{ex.StackTrace}");

                    if (Debugger.IsAttached)
                        Debugger.Break();
                }
            }

            // If the driver is still null, fall back.
            if (driver == null)
            {
                PlatformComponents.PreferSDL2 = Singleton.PreferSDL2;
                driver = component switch {
                    PalComponents.OpenGL => PlatformComponents.CreateOpenGLComponent(),
                    // PalComponents.Vulkan => PlatformComponents.CreateVulkanComponent(),
                    PalComponents.WindowIcon => PlatformComponents.CreateIconComponent(),
                    PalComponents.MouseCursor => PlatformComponents.CreateCursorComponent(),
                    PalComponents.Window => PlatformComponents.CreateWindowComponent(),
                    PalComponents.Surface => PlatformComponents.CreateSurfaceComponent(),
                    PalComponents.Display => PlatformComponents.CreateDisplayComponent(),
                    PalComponents.MiceInput => PlatformComponents.CreateMouseComponent(),
                    PalComponents.KeyboardInput => PlatformComponents.CreateKeyboardComponent(),
                    // PalComponents.ControllerInput => PlatformComponents.CreateControllerComponent(),
                    PalComponents.Clipboard => PlatformComponents.CreateClipboardComponent(),
                    PalComponents.Shell => PlatformComponents.CreateShellComponent(),
                    PalComponents.Joystick => PlatformComponents.CreateJoystickComponent(),
                    _ => null
                };
            }

            return driver;
        }
    }
}