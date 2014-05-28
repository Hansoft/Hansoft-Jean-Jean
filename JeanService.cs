using System;
using System.Reflection;
using System.Configuration;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml;

using HPMSdk;

using Hansoft.SimpleLogging;
using Hansoft.ObjectWrapper;
using Hansoft.Jean.Behavior;

namespace Hansoft.Jean
{
    public class JeanService
    {
        string sdkUser;
        string sdkUserPwd;
        string server;
        int portNumber;
        string databaseName;
        int eventWindow;

        List<AbstractBehavior> behaviors;
        List<Type> behaviorTypes;
        List<string> extensionAssemblies;

        Semaphore startSemaphore;
        HPMCallbackHandler callbackHandler;
        Semaphore callbackSemaphore;
        bool stopped = false;
        Thread processingThread;

        EventLogLogger logger;

        bool loadingError;
        bool startupError;

        public JeanService()
        {
            logger = new EventLogLogger();
            logger.Initialize("Jean");
            try
            {
                loadingError = false;
                startupError = true;
                LoadSettings();
                callbackSemaphore = new Semaphore(0, 1);
                startSemaphore = new Semaphore(0, 1);
                processingThread = new Thread(this.EventProcessingLoop);
                processingThread.Start();
            }
            catch (Exception e)
            {
                loadingError = true;
                logger.Exception("Jean could not be loaded.", e);
            }
            logger.Information("Jean was loaded");
        }

        public void OnProjectCreateCompleted(object sender, EventArgs e)
        {
            InitializeBehaviors();
        }

        public void OnProjectDeleteCompleted(object sender, EventArgs e)
        {
            InitializeBehaviors();
        }

        public void InitializeBehaviors()
        {
            foreach (AbstractBehavior b in behaviors)
            {
                try
                {
                    b.Initialize(callbackHandler.BufferEvents, extensionAssemblies, logger);
                }
                catch (Exception e)
                {
                    logger.Exception("Error when initializing behavior " + b.Title + ". The behavior will not be applied.", e);
                }
            }
        }

        public void Start()
        {
            if (!loadingError)
            {
                startupError = false;
                try
                {
                    callbackHandler = new HPMCallbackHandler(eventWindow);
                    callbackHandler.ProjectCreateCompleted += new System.EventHandler<EventArgs>(OnProjectCreateCompleted);
                    SessionManager.Initialize(sdkUser, sdkUserPwd, server, portNumber, databaseName);

                    if (SessionManager.Instance.Connect(callbackHandler, callbackSemaphore))
                    {
                        //TODO: The hooking up of the event handlers should be pushed down into a registration function in CallbackHandler
                        foreach (AbstractBehavior b in behaviors)
                        {
                            callbackHandler.TaskChange += new System.EventHandler<TaskChangeEventArgs>(b.OnTaskChange);
                            callbackHandler.TaskChangeCustomColumnData += new System.EventHandler<TaskChangeCustomColumnDataEventArgs>(b.OnTaskChangeCustomColumnData);
                            callbackHandler.TaskCreate += new System.EventHandler<TaskCreateEventArgs>(b.OnTaskCreate);
                            callbackHandler.TaskMove += new System.EventHandler<TaskMoveEventArgs>(b.OnTaskMove);
                            callbackHandler.DataHistoryReceived += new System.EventHandler<DataHistoryReceivedEventArgs>(b.OnDataHistoryReceived);
                            callbackHandler.ProjectCreate += new System.EventHandler<ProjectCreateEventArgs>(b.OnProjectCreate);
                            callbackHandler.TaskDelete += new System.EventHandler<TaskDeleteEventArgs>(b.OnTaskDelete);
                            callbackHandler.BeginProcessBufferedEvents += new System.EventHandler<EventArgs>(b.OnBeginProcessBufferedEvents);
                            callbackHandler.EndProcessBufferedEvents += new System.EventHandler<EventArgs>(b.OnEndProcessBufferedEvents);
                            InitializeBehaviors();
                        }

                        startSemaphore.Release();
                        logger.Information("Jean connected to " + server + ": " + portNumber + " (" + databaseName + ")");
                    }
                    else
                    {
                        startupError = true;
                        logger.Warning("Could not connect to the Hansoft server with the specified connection parameters.");
                    }
                }
                catch (Exception e)
                {
                    startupError = true;
                    logger.Exception("Jean could not be started.", e);
                }
            }
            else
                logger.Warning("It was not possible to start Jean due to a previous error when loading the service");
        }

        public void Stop()
        {
            if (!loadingError)
            {
                if (callbackHandler != null)
                    callbackHandler.Dispose();
                callbackHandler = null;
                if (SessionManager.Instance.Connected)
                    SessionManager.Instance.CloseSession();
                stopped = true;
                if (!startupError) 
                    callbackSemaphore.Release();
                logger.Information("Jean for Hansoft was stopped");
            }
        }

        public void EventProcessingLoop()
        {
            startSemaphore.WaitOne();
            while (!stopped)
            {
                // Have a timer that signals the semaphore also
                callbackSemaphore.WaitOne();
                if (!stopped && ! startupError)
                {
                    EHPMError result = SessionManager.Session.SessionProcess();
                    int iSeconds = 0;
                    if (result == EHPMError.ConnectionLost)
                    {
                        do
                        {
                            logger.Warning("The connection to the Hansoft server was lost. A reconnection attempt will be made in 10 seconds.");
                            Thread.Sleep(10000);
                            iSeconds += 10;
                        }
                        while (!SessionManager.Instance.Reconnect());
                        logger.Information("The connection to the Hansoft server was restored after " + iSeconds + " seconds.");
                    }
                    else if (result != EHPMError.NoError)
                        logger.Warning("SessionProcess returned an error code: " + result.ToString());
                }
                else
                    SessionManager.Instance.CloseSession();
            }
        }

        void LoadSettings()
        {
            string currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            FileInfo fInfo = new FileInfo(Path.Combine(currentDirectory, "JeanSettings.xml"));
            if (fInfo.Exists)
            {
                XmlDocument xmlDocument = new XmlDocument();
                xmlDocument.Load(fInfo.FullName);
                XmlElement documentElement = xmlDocument.DocumentElement;
                if (documentElement.Name != "Configuration")
                    throw new FormatException("The root element of the settings file must be of type Configuration, got " + documentElement.Name);

                XmlNodeList topNodes = documentElement.ChildNodes;

                foreach (XmlNode node in topNodes)
                {
                    if (node.NodeType == XmlNodeType.Element)
                    {
                        XmlElement el = (XmlElement)node;
                        switch (el.Name)
                        {
                            case ("Connection"):
                                server = el.GetAttribute("HansoftServerHost");
                                portNumber = Int32.Parse(el.GetAttribute("HansoftServerPort"));
                                databaseName = el.GetAttribute("HansoftDatabase");
                                sdkUser = el.GetAttribute("HansoftSdkUser");
                                sdkUserPwd = el.GetAttribute("HansoftSdkUserPassword");
                                eventWindow = Int32.Parse(el.GetAttribute("EventWindow"));
                                break;
                            case ("Behaviors"):
                                behaviors = new List<AbstractBehavior>();
                                foreach (XmlNode bNode in el.ChildNodes)
                                {
                                    if (bNode.NodeType == XmlNodeType.Element)
                                    {
                                        XmlElement bEl = (XmlElement)bNode;
                                        try
                                        {
                                            AbstractBehavior b = BehaviorFactory(bEl);
                                            if (b != null)
                                                behaviors.Add(b);
                                        }
                                        catch (Exception e)
                                        {
                                            logger.Exception("Error in configuration file JeanSettings.xml when loading behavior from element " + bEl.Name + ".", e);
                                        }
                                    }
                                }
                                break;
                            case ("LoadAssemblies"):
                                extensionAssemblies = new List<string>();
                                behaviorTypes = new List<Type>();
                                foreach (XmlNode aNode in el.ChildNodes)
                                {
                                    if (aNode.NodeType == XmlNodeType.Element)
                                    {
                                        XmlElement aEl = (XmlElement)aNode;
                                        LoadAssembly(aEl, currentDirectory);
                                    }
                                }
                                break;
                            default:
                                throw new FormatException("Error in configuration file JeanSettings.xml. Expected element of type Connection, Behaviors, or LoadAssemblies, got " + el.Name);
                        }
                    }
                }
            }
            else
                throw new ArgumentException("Could not find settings file JeanSettings.xml");
        }

        private AbstractBehavior BehaviorFactory(XmlElement configuration)
        {
            Type foundType = behaviorTypes.Find(t => t.Name == configuration.Name);
            if (foundType != null)
            {
                ConstructorInfo cInfo = foundType.GetConstructor(new Type[] { typeof(XmlElement) });
                return (AbstractBehavior)cInfo.Invoke(new object[] { configuration });
            }
            else
                throw new FormatException("Unknown behavior " + configuration.Name);
        }

        private void LoadAssembly(XmlElement el, string currentDirectory)
        {
            if (el.Name == "Assembly" && el.HasAttribute("Path"))
            {
                string path = el.GetAttribute("Path");
                FileInfo fInfo = new FileInfo(Path.Combine(currentDirectory, path));
                if (!fInfo.Exists)
                    fInfo = new FileInfo(path);
                if (fInfo.Exists)
                {
                    try
                    {
                        Assembly loadedAssembly = Assembly.LoadFrom(fInfo.FullName);
                        bool wasBehaviorAssembly = false;
                        foreach (Type type in loadedAssembly.DefinedTypes)
                        {
                            if (type.BaseType != null && type.BaseType.FullName == "Hansoft.Jean.Behavior.AbstractBehavior")
                            {
                                behaviorTypes.Add(type);
                                wasBehaviorAssembly = true;
                            }
                        }
                        if (!wasBehaviorAssembly)
                        {
                            extensionAssemblies.Add(path);
                        }
                    }
                    catch (Exception e)
                    {
                        logger.Exception("Error in configuration file JeanSettings.xml when loading assembly " + fInfo.FullName + ".", e);
                    }
                }
                else
                    logger.Error("Error in configuration file JeanSettings.xml. Could not find assembly file: " + path);
            }
            else
                logger.Error("Error in configuration file JeanSettings.xml. Malformed configuration of assemblies: " + el.ToString());
        }
    }
}
