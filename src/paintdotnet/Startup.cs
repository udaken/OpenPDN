/////////////////////////////////////////////////////////////////////////////////
// Paint.NET                                                                   //
// Copyright (C) dotPDN LLC, Rick Brewster, Tom Jackson, and contributors.     //
// Portions Copyright (C) Microsoft Corporation. All Rights Reserved.          //
// See src/Resources/Files/License.txt for full licensing and attribution      //
// details.                                                                    //
// .                                                                           //
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaintDotNet
{
    internal sealed class Startup
    {
        private static Startup instance;
        private static DateTime startupTime;
        private string[] args;

        private Startup(string[] args)
        {
            this.args = args;
        }

        public void Start()
        {
            // Set up unhandled exception handlers
#if DEBUG
            // In debug builds we'd prefer to have it dump us into the debugger
#else
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);
            Application.ThreadException += new ThreadExceptionEventHandler(Application_ThreadException);
#endif

            // Initialize some misc. Windows Forms settings
            Application.SetCompatibleTextRenderingDefault(false);
            Application.EnableVisualStyles();

            // The rest of the code is put in a separate method so that certain DLL's
            // won't get delay loaded until after we try to do repairs.
            var doc = LoadDocument(null, args[0], out var plugin, null);
            Debug.WriteLine(
                $"LayerCount: {doc.Layers.Count}\n" +
                $"Layer: {doc.Size}");
        }

        public static Document LoadDocument(Control owner, string fileName, out FileType fileTypeResult, ProgressEventHandler progressCallback)
        {
            FileTypeCollection fileTypes;
            int ftIndex;
            FileType fileType;

            fileTypeResult = null;

            try
            {
                fileTypes = FileTypes.GetFileTypes();
                ftIndex = fileTypes.IndexOfExtension(Path.GetExtension(fileName));

                if (ftIndex == -1)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.ImageTypeNotRecognized"));
                    return null;
                }

                fileType = fileTypes[ftIndex];
                fileTypeResult = fileType;
            }

            catch (ArgumentException)
            {
                string format = PdnResources.GetString("LoadImage.Error.InvalidFileName.Format");
                string error = string.Format(format, fileName);
                Utility.ErrorBox(owner, error);
                return null;
            }

            Document document = null;

            using (new WaitCursorChanger(owner))
            {
                Utility.GCFullCollect();
                Stream stream = null;

                try
                {
                    try
                    {
                        stream = new FileStream(fileName, FileMode.Open, FileAccess.Read);
                        long totalBytes = 0;

                        SiphonStream siphonStream = new SiphonStream(stream);

                        IOEventHandler ioEventHandler = null;
                        ioEventHandler =
                            delegate (object sender, IOEventArgs e)
                            {
                                if (progressCallback != null)
                                {
                                    totalBytes += (long)e.Count;
                                    double percent = Utility.Clamp(100.0 * ((double)totalBytes / (double)siphonStream.Length), 0, 100);
                                    progressCallback(null, new ProgressEventArgs(percent));
                                }
                            };

                        siphonStream.IOFinished += ioEventHandler;

                        using (new WaitCursorChanger(owner))
                        {
                            document = fileType.Load(siphonStream);

                            if (progressCallback != null)
                            {
                                progressCallback(null, new ProgressEventArgs(100.0));
                            }
                        }

                        siphonStream.IOFinished -= ioEventHandler;
                        siphonStream.Close();
                    }

                    catch (WorkerThreadException ex)
                    {
                        Type innerExType = ex.InnerException.GetType();
                        ConstructorInfo ci = innerExType.GetConstructor(new Type[] { typeof(string), typeof(Exception) });

                        if (ci == null)
                        {
                            throw;
                        }
                        else
                        {
                            Exception ex2 = (Exception)ci.Invoke(new object[] { "Worker thread threw an exception of this type", ex.InnerException });
                            throw ex2;
                        }
                    }
                }

                catch (ArgumentException)
                {
                    if (fileName.Length == 0)
                    {
                        Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.BlankFileName"));
                    }
                    else
                    {
                        Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.ArgumentException"));
                    }
                }

                catch (UnauthorizedAccessException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.UnauthorizedAccessException"));
                }

                catch (System.Security.SecurityException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.SecurityException"));
                }

                catch (FileNotFoundException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.FileNotFoundException"));
                }

                catch (DirectoryNotFoundException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.DirectoryNotFoundException"));
                }

                catch (PathTooLongException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.PathTooLongException"));
                }

                catch (IOException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.IOException"));
                }

                catch (System.Runtime.Serialization.SerializationException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.SerializationException"));
                }

                catch (OutOfMemoryException)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.OutOfMemoryException"));
                }

                catch (Exception)
                {
                    Utility.ErrorBox(owner, PdnResources.GetString("LoadImage.Error.Exception"));
                }

                finally
                {
                    if (stream != null)
                    {
                        stream.Close();
                        stream = null;
                    }
                }
            }

            return document;
        }


        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            startupTime = DateTime.Now;

#if !DEBUG
            try
            {
#endif
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

            instance = new Startup(args);
            instance.Start();
#if !DEBUG
            }

            catch (Exception ex)
            {
                try
                {
                    UnhandledException(ex);
                    Process.GetCurrentProcess().Kill();
                }

                catch (Exception)
                {
                    MessageBox.Show(ex.ToString());
                    Process.GetCurrentProcess().Kill();
                }
            }
#endif

            return 0;
        }

        private static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            // For v3.05, we renamed PdnLib.dll to PaintDotNet.Core.dll. So we should really make
            // sure we stay compatible with old plugin DLL's.
            const string oldCoreName = "PdnLib";

            return args.Name.StartsWith(oldCoreName) ? typeof(ColorBgra).Assembly : null;
        }

        private static void UnhandledException(Exception ex)
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            const string fileName = "pdncrash.log";
            string fullName = Path.Combine(dir, fileName);

            using (StreamWriter stream = new System.IO.StreamWriter(fullName, true))
            {
                stream.AutoFlush = true;
            }

            string errorFormat;
            string errorText;

            try
            {
                errorFormat = PdnResources.GetString("Startup.UnhandledError.Format");
            }

            catch (Exception)
            {
                errorFormat = InvariantStrings.StartupUnhandledErrorFormatFallback;
            }

            errorText = string.Format(errorFormat, fileName);
            Utility.ErrorBox(null, errorText);
        }


        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            UnhandledException((Exception)e.ExceptionObject);
            Process.GetCurrentProcess().Kill();
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            UnhandledException(e.Exception);
            Process.GetCurrentProcess().Kill();
        }
    }
}
