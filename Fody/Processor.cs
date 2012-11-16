using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Build.Framework;

public partial class Processor
{
    public string AssemblyFilePath;
    public string IntermediateDirectoryPath;
    public string KeyFilePath;
    public string MessageImportance = "Low";
    public string ProjectFilePath;
    public string References;
    public string SolutionDirectoryPath;
    public IBuildEngine BuildEngine;

    AddinFinder addinFinder;

    public BuildLogger Logger;
    static object locker;

    static AppDomain appDomain;
    public ContainsTypeChecker ContainsTypeChecker = new ContainsTypeChecker();

    static Processor()
    {
        locker = new object();
        DomainAssemblyResolver.Connect();
    }

    public bool Execute()
    {
        BuildEngine.LogMessageEvent(new BuildMessageEventArgs(string.Format("Fody (version {0}) Executing", GetType().Assembly.GetName().Version), "", "Fody", Microsoft.Build.Framework.MessageImportance.High));

        var stopwatch = Stopwatch.StartNew();

        Logger = new BuildLogger(MessageImportance)
                     {
                         BuildEngine = BuildEngine,
                     };

        try
        {
            Inner();
            return !Logger.ErrorOccurred;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception.ToFriendlyString());
            return false;
        }
        finally
        {
            stopwatch.Stop();
            Logger.Flush();
            BuildEngine.LogMessageEvent(new BuildMessageEventArgs(string.Format("\tFinished Fody {0}ms.", stopwatch.ElapsedMilliseconds), "", "Fody", Microsoft.Build.Framework.MessageImportance.High));
        }
    }

    void Inner()
    {
        ValidateProjectPath();

        ValidatorAssemblyPath();

        FindProjectWeavers();
        
        if (!ShouldStartSinceFileChanged())
        {
            if (!CheckForWeaversXmlChanged())
            {
                
                FindWeavers();
        
                if (WeaversHistory.HasChanged(Weavers.Select(x => x.AssemblyPath)))
                {
                    Logger.LogWarning("A re-build is required to because a weaver changed");
                }
            }
            return;
        }

        ValidateSolutionPath();

        FindWeavers();

        if (Weavers.Count == 0)
        {
            Logger.LogWarning(string.Format("Could not find any weavers. Either add a project named 'Weavers' with a type named 'ModuleWeaver' or add some items to '{0}'.", "FodyWeavers.xml"));
            return;
        }

        lock (locker)
        {
            ExecuteInOwnAppDomain();
        }
        FlushWeaversXmlHistory();
    }

    void FindWeavers()
    {
        ReadProjectWeavers();
        addinFinder = new AddinFinder
            {
                Logger = Logger, 
                SolutionDirectoryPath = SolutionDirectoryPath
            };
        addinFinder.FindAddinDirectories();

        FindWeaverProjectFile();


        ConfigureWhenWeaversFound();

        ConfigureWhenNoWeaversFound();
    }


    void ExecuteInOwnAppDomain()
    {
        if (WeaversHistory.HasChanged(Weavers.Select(x => x.AssemblyPath)) || appDomain == null)
        {
			Logger.LogInfo("A Weaver HasChanged so loading a new AppDomian");
            if (appDomain != null)
            {
                AppDomain.Unload(appDomain);
            }

            var appDomainSetup = new AppDomainSetup
                                     {
                                         ApplicationBase = AssemblyLocation.CurrentDirectory(),
                                     };
            appDomain = AppDomain.CreateDomain("Fody", null, appDomainSetup);
        }
        var innerWeaver = (IInnerWeaver) appDomain.CreateInstanceAndUnwrap("FodyIsolated", "InnerWeaver");
        innerWeaver.AssemblyFilePath = AssemblyFilePath;
        innerWeaver.References = References;
        innerWeaver.KeyFilePath = KeyFilePath;
        innerWeaver.Logger = Logger;
        innerWeaver.SolutionDirectoryPath = SolutionDirectoryPath;
        innerWeaver.ProjectFilePath = ProjectFilePath;
        innerWeaver.Weavers = Weavers;
        innerWeaver.IntermediateDirectoryPath = IntermediateDirectoryPath;
        innerWeaver.Execute();
    }
}