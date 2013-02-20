using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Fody;
using MethodTimer;
using Microsoft.Build.Framework;
using MSMessageEnum = Microsoft.Build.Framework.MessageImportance;

public partial class Processor
{
    public string AssemblyFilePath;
    public string IntermediateDirectoryPath;
    public string KeyFilePath;
    public bool SignAssembly;
    public string MessageImportance = "Low";
    public string ProjectDirectory;
    public string References;
    public string SolutionDirectoryPath;
    public IBuildEngine BuildEngine;
    public List<string> ReferenceCopyLocalPaths;
    public bool DebugLoggingEnabled;
    public List<string> DefineConstants;

    AddinFinder addinFinder;
    static Dictionary<string, AppDomain> solutionDomains = new Dictionary<string, AppDomain>(StringComparer.OrdinalIgnoreCase);

    public BuildLogger Logger;
    static object locker;

    public ContainsTypeChecker ContainsTypeChecker = new ContainsTypeChecker();

    static Processor()
    {
        locker = new object();
        DomainAssemblyResolver.Connect();
    }

    public bool Execute()
    {
        var executingMessage = string.Format("Fody (version {0}) Executing", typeof (Processor).Assembly.GetName().Version);
        BuildEngine.LogMessageEvent(new BuildMessageEventArgs(executingMessage, "", "Fody", MSMessageEnum.High));

        var stopwatch = Stopwatch.StartNew();

        Logger = new BuildLogger(MessageImportance)
                     {
                         BuildEngine = BuildEngine,
                     };

        if (DebugLoggingEnabled)
        {
            MethodTimeLogger.LogDebug = s => Logger.LogInfo(s);
        }
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
            var finishedMessage = string.Format("\tFinished Fody {0}ms.", stopwatch.ElapsedMilliseconds);
            Logger.Flush();
            BuildEngine.LogMessageEvent(new BuildMessageEventArgs(finishedMessage, "", "Fody", MSMessageEnum.High));
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
            Logger.LogError(string.Format("You don't seem to have configured any weavers. Try adding a Fody nuget package to '{0}'. Have a look here for the list of available packages.", "FodyWeavers.xml"));
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
        var stopwatch = Stopwatch.StartNew();
        Logger.LogInfo("Finding weavers");
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

        Logger.LogInfo(string.Format("Finished finding weavers {0}ms", stopwatch.ElapsedMilliseconds));
    }

    [Time]
    void ExecuteInOwnAppDomain()
    {
        AppDomain appdomain;
        if (solutionDomains.TryGetValue(SolutionDirectoryPath, out appdomain))
        {
            if (WeaversHistory.HasChanged(Weavers.Select(x => x.AssemblyPath)))
            {
                Logger.LogInfo("A Weaver HasChanged so loading a new AppDomian");
                AppDomain.Unload(appdomain);
                appdomain = solutionDomains[SolutionDirectoryPath] = CreateDomain();
            }
        }
        else
        {
            appdomain = solutionDomains[SolutionDirectoryPath] = CreateDomain();
        }

        var innerWeaver = (IInnerWeaver) appdomain.CreateInstanceAndUnwrap("FodyIsolated", "InnerWeaver");
        innerWeaver.AssemblyFilePath = AssemblyFilePath;
        innerWeaver.References = References;
        innerWeaver.KeyFilePath = KeyFilePath;
        innerWeaver.ReferenceCopyLocalPaths = ReferenceCopyLocalPaths;
        innerWeaver.SignAssembly = SignAssembly;
        innerWeaver.Logger = Logger;
        innerWeaver.SolutionDirectoryPath = SolutionDirectoryPath;
        innerWeaver.Weavers = Weavers;
        innerWeaver.IntermediateDirectoryPath = IntermediateDirectoryPath;
        innerWeaver.DefineConstants = DefineConstants;
        innerWeaver.Execute();
    }

    AppDomain CreateDomain()
    {
        Logger.LogInfo("Creating a new AppDomian");
        var appDomainSetup = new AppDomainSetup
        {
            ApplicationBase = AssemblyLocation.CurrentDirectory(),
        };
        return AppDomain.CreateDomain(string.Format("Fody Domain for '{0}'", SolutionDirectoryPath), null, appDomainSetup);
    }
}