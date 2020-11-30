// msfastbuild.cs - Generates and executes a bff file for fastbuild from a .sln or .vcxproj.
// Copyright 2016 Liam Flookes and Yassine Riahi
// Available under an MIT license. See license file on github for details.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommandLine;
using CommandLine.Text;
using System.IO;
using System.Reflection;
using System.Text;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Construction;
using Microsoft.Build.Utilities;

namespace msfastbuild
{
	public class Options
	{
		[Option('p', "vcproject", DefaultValue = "",
		HelpText = "Path of .vcxproj file to build, or project name if a solution is provided.")]
		public string Project { get; set; }

		[Option('s', "sln", DefaultValue = "",
		HelpText = "Path of .sln file which contains the projects.")]
		public string Solution { get; set; }

		[Option('c', "config", DefaultValue = "Debug",
		HelpText = "Configuration to build.")]
		public string Config { get; set; }

		[Option('f', "platform", DefaultValue = "Win32",
		HelpText = "Platform to build.")]
		public string Platform { get; set; }

		[Option('a', "fbargs", DefaultValue = "-dist",
		HelpText = "Arguments that pass through to FASTBuild.")]
		public string FBArgs { get; set; }

		[Option('b', "brokerage", DefaultValue = "",
		HelpText = "FASTBUILD_BROKERAGE_PATH for distributed compilation")]
		public string Brokerage { get; set; }

		[Option('g', "generateonly", DefaultValue = false,
		HelpText = "Generate bff file only, without calling FASTBuild.")]
		public bool GenerateOnly { get; set; }

		[Option('r', "regen", DefaultValue = false,
		HelpText = "Regenerate bff file even when the project hasn't changed.")]
		public bool AlwaysRegenerate { get; set; }

		[Option('e', "fbexepath", DefaultValue = @"FBuild.exe",
		HelpText = "Path to FASTBuild executable.")]
		public string FBExePath { get; set; }

		[Option('u', "unity", DefaultValue = false,
		HelpText = "Whether to combine files into a unity step. May substantially improve compilation time, but not all projects are suitable.")]
		public bool UseUnity { get; set; }

		[Option('m', "maxprocess", DefaultValue = 1u,
		HelpText = "Max process to executable FASTBuild.")]
		public uint MaxProcess { get; set; }

		[HelpOption]
		public string GetUsage()
		{
			return HelpText.AutoBuild(this,(HelpText current) => HelpText.DefaultParsingErrorsHandler(this, current));
		}
	}

	public class msfastbuild
	{
		static public string PlatformToolsetVersion = "140";
		static public string VCBasePath = "";
		static public string VCExePath = "";
		static public string bffOutputFilePath = "fbuild.bff";
		static public Options CommandLineOptions = new Options();
		static public string WindowsSDKTarget = "10.0.10240.0";
		static public MSFBProject CurrentProject;
		static public Assembly CPPTasksAssembly;
		static public string PreBuildBatchFile = "";
		static public string SolutionDir = "";
		static public bool HasCompileActions = true;

		public enum BuildType
		{
		    Application,
		    StaticLib,
		    DynamicLib
		}


		public class MSFBProject
		{
			public Project Proj;
			public List<MSFBProject> dependProjects = new List<MSFBProject>();
			public string additionalLinkInputs = "";
		}

		static List<string> GetTargetProjects()
		{
            List<string> targetProjects = new List<string>();
			if (string.IsNullOrEmpty(CommandLineOptions.Solution) || !File.Exists(CommandLineOptions.Solution))
			{
				if (!string.IsNullOrEmpty(CommandLineOptions.Project))
				{
					targetProjects.Add(Path.GetFullPath(CommandLineOptions.Project));
				}
				return targetProjects;
			}

            try
            {
                List<ProjectInSolution> solutionProjects = SolutionFile.Parse(Path.GetFullPath(CommandLineOptions.Solution)).ProjectsInOrder.Where(el => el.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat).ToList();
                List<ProjectInSolution> relatedProjects = new List<ProjectInSolution>();
                if (string.IsNullOrEmpty(CommandLineOptions.Project))
                {
                    relatedProjects = solutionProjects;
                }
                else
                {
                    ProjectInSolution project = solutionProjects.First(proj => Path.GetFileName(proj.AbsolutePath) == CommandLineOptions.Project);
                    if (project != null)
                        relatedProjects.Add(project);
                    for (int i = 0; i < relatedProjects.Count; ++i)
                    {
                        foreach (var guid in relatedProjects[i].Dependencies)
                        {
                            project = solutionProjects.First(proj => proj.ProjectGuid == guid);
                            if (project != null && !relatedProjects.Contains(project))
                                relatedProjects.Add(project);
                        }
                    }
                }

                List<ProjectInSolution> sortedProjects = new List<ProjectInSolution>();
                Dictionary<string, List<string>> dependedProjects = new Dictionary<string, List<string>>();
                foreach (var project in relatedProjects)
                    dependedProjects[project.ProjectGuid] = new List<string>(project.Dependencies);
                while (dependedProjects.Count > 0)
                {
                    var item = dependedProjects.First(pair => pair.Value.Count == 0); // all depend projects has marked
                    ProjectInSolution project = solutionProjects.First(proj => proj.ProjectGuid == item.Key);
                    dependedProjects.Remove(item.Key);
                    sortedProjects.Add(project);
                    foreach (var depends in dependedProjects.Values)
                        depends.Remove(project.ProjectGuid);
                }
                targetProjects = sortedProjects.ConvertAll(el => el.AbsolutePath);
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to parse solution file " + CommandLineOptions.Solution + "!");
                Console.WriteLine("Exception: " + e.Message);
                return null;
            }

			return targetProjects;
		}

		static void Main(string[] args)
		{
			Parser parser = new Parser();
			if (!parser.ParseArguments(args, CommandLineOptions))
			{
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			if (string.IsNullOrEmpty(CommandLineOptions.Solution) && string.IsNullOrEmpty(CommandLineOptions.Project))
			{
				Console.WriteLine("No solution or project provided!");
				Console.WriteLine(CommandLineOptions.GetUsage());
				return;
			}

			var targetProjects = GetTargetProjects();
			if (targetProjects == null)
				return;

			SolutionDir = Path.GetDirectoryName(Path.GetFullPath(CommandLineOptions.Solution));
			SolutionDir = SolutionDir.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (SolutionDir.Last() != Path.AltDirectorySeparatorChar)
				SolutionDir += Path.AltDirectorySeparatorChar;

            List<MSFBProject> evaluatedProjects = new List<MSFBProject>();
			for (int i = 0; i < targetProjects.Count; ++i)
			{
				EvaluateProjectReferences(targetProjects[i], evaluatedProjects, null);
			}

			int buildProjectCount = 0;
			List<MSFBProject> needBuildProjects = new List<MSFBProject>();
			foreach(MSFBProject project in evaluatedProjects)
			{
				CurrentProject = project;

				string VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPathEffective");
				if (string.IsNullOrEmpty(VCTargetsPath))
				{
					VCTargetsPath = CurrentProject.Proj.GetPropertyValue("VCTargetsPath");
				}
				if (string.IsNullOrEmpty(VCTargetsPath))
				{
					Console.WriteLine("Failed to evaluate VCTargetsPath variable on " + Path.GetFileName(CurrentProject.Proj.FullPath) + "!");
					continue;
				}
				// TODO
				WindowsSDKTarget = project.Proj.GetProperty("WindowsTargetPlatformVersion") != null ? project.Proj.GetProperty("WindowsTargetPlatformVersion").EvaluatedValue : "8.1";
				PlatformToolsetVersion = project.Proj.GetProperty("PlatformToolsetVersion").EvaluatedValue;
				VCBasePath = project.Proj.GetProperty("VCInstallDir").EvaluatedValue;
				if (CommandLineOptions.Platform.ToLower() != "x64")
				{
					VCExePath = project.Proj.GetProperty("VC_ExecutablePath_x86_x86").EvaluatedValue;
				}
				else
				{
					VCExePath = project.Proj.GetProperty("VC_ExecutablePath_x64_x64").EvaluatedValue;
				}

                bool useBuiltinDll = true;
				string BuildDllName = "Microsoft.Build.CPPTasks.Common.dll";
				string BuildDllPath = VCTargetsPath + BuildDllName;
				if (File.Exists(BuildDllPath))
				{
					CPPTasksAssembly = Assembly.LoadFrom(BuildDllPath);
					if (CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link") != null &&
						CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB") != null)
					{
						useBuiltinDll = false;
					}
				}
				if (useBuiltinDll)
				{
					CPPTasksAssembly = Assembly.LoadFrom(AppDomain.CurrentDomain.BaseDirectory + BuildDllName);
				}

				bffOutputFilePath = Path.GetDirectoryName(CurrentProject.Proj.FullPath) + "\\" + Path.GetFileName(CurrentProject.Proj.FullPath) + "_" + CommandLineOptions.Config.Replace(" ", "") + "_" + CommandLineOptions.Platform.Replace(" ", "") + ".bff";
				GenerateBffFromVcxproj(project, CommandLineOptions.Config, CommandLineOptions.Platform);

				if (!CommandLineOptions.GenerateOnly)
				{
					/*if (HasCompileActions)
						needBuildProjects.Add(CurrentProject);*/
                    if (HasCompileActions && !ExecuteBffFile(CurrentProject.Proj.FullPath, CommandLineOptions.Platform))
						break;
					else
						buildProjectCount++;
                }
			}

			List<Process> runningProcesses = new List<Process>();
			uint maxProcess = Math.Max(CommandLineOptions.MaxProcess, 1);
			while (needBuildProjects.Count > 0 || runningProcesses.Count > 0)
			{
				if (needBuildProjects.Count > 0 && runningProcesses.Count < maxProcess)
				{
					var project = needBuildProjects[0];
					needBuildProjects.RemoveAt(0);
					Console.WriteLine("Create process of " + project.Proj.FullPath);
                    var process = GenWorkProcess(project.Proj.FullPath, CommandLineOptions.Platform);
					try
					{
						process.Start();
						runningProcesses.Add(process);
					}
					catch (Exception e)
					{
						Console.WriteLine("Failed to launch FASTBuild!");
						Console.WriteLine("Exception: " + e.Message);
						break;
					}
                }

				var itor = runningProcesses.GetEnumerator();
				if (itor.MoveNext())
				{
					var process = itor.Current;
                    if (!process.StandardOutput.EndOfStream)
					{
						Console.Write("[" + process.Id + "] " + process.StandardOutput.ReadLine() + "\n");
					}
					else
					{
						process.WaitForExit();
						runningProcesses.Remove(process);
						if (process.ExitCode == 0)
						{
							buildProjectCount++;
						}
                    }
                }
			}
			Console.Write(buildProjectCount + "/" + evaluatedProjects.Count + " built.");
			if (needBuildProjects.Count > 0)
			{
				Console.WriteLine("");
				Console.Write("Unbuild projects: ");
				foreach (var project in needBuildProjects)
				{
					Console.WriteLine("\t" + project.Proj.FullPath);
				}
			}
			Console.WriteLine("");
		}

		public static void EvaluateProjectReferences(string ProjectPath, List<MSFBProject> evaluatedProjects, MSFBProject dependent)
		{
			if (!string.IsNullOrEmpty(ProjectPath) && File.Exists(ProjectPath))
			{
				try
				{
					MSFBProject newProj = evaluatedProjects.Find(elem => elem.Proj.FullPath == Path.GetFullPath(ProjectPath));
					if (newProj != null)
					{
						//Console.WriteLine("Found exisiting project " + Path.GetFileNameWithoutExtension(ProjectPath));
						if (dependent != null)
							newProj.dependProjects.Add(dependent);
					}
					else
					{
						ProjectCollection projColl = new ProjectCollection();
						if (!string.IsNullOrEmpty(SolutionDir))
							projColl.SetGlobalProperty("SolutionDir", SolutionDir);
						newProj = new MSFBProject();
						Project proj = projColl.LoadProject(ProjectPath);

						if (proj != null)
						{
							proj.SetGlobalProperty("Configuration", CommandLineOptions.Config);
							proj.SetGlobalProperty("Platform", CommandLineOptions.Platform);
							if (!string.IsNullOrEmpty(SolutionDir))
								proj.SetGlobalProperty("SolutionDir", SolutionDir);
							proj.ReevaluateIfNecessary();

							newProj.Proj = proj;
							if (dependent != null)
							{
								newProj.dependProjects.Add(dependent);
							}
							var ProjectReferences = proj.Items.Where(elem => elem.ItemType == "ProjectReference");
							foreach (var ProjRef in ProjectReferences)
							{
								if (ProjRef.GetMetadataValue("ReferenceOutputAssembly") == "true" || ProjRef.GetMetadataValue("LinkLibraryDependencies") == "true")
								{
									//Console.WriteLine(string.Format("{0} referenced by {1}.", Path.GetFileNameWithoutExtension(ProjRef.EvaluatedInclude), Path.GetFileNameWithoutExtension(proj.FullPath)));
									EvaluateProjectReferences(Path.GetDirectoryName(proj.FullPath) + Path.DirectorySeparatorChar + ProjRef.EvaluatedInclude, evaluatedProjects, newProj);
								}
							}
							//Console.WriteLine("Adding " + Path.GetFileNameWithoutExtension(proj.FullPath));
							evaluatedProjects.Add(newProj);
						}
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Failed to parse project file " + ProjectPath + "!");
					Console.WriteLine("Exception: " + e.Message);
					return;
				}
			}
		}

		public static bool HasFileChanged(string inputFile, string platform, string config, out string md5hash)
		{
			using (var md5 = System.Security.Cryptography.MD5.Create())
			{
				using (var stream = File.OpenRead(inputFile))
				{
				    md5hash = ";" + inputFile + "_" + platform + "_" + config + "_" + BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLower();
				}
			}
			
			if (!File.Exists(bffOutputFilePath))
				return true;
			string firstLine = File.ReadLines(bffOutputFilePath).First();
			return firstLine != md5hash;
		}

		protected static string GetBatchFileHeadText(string platform, string windowsSdkVersion)
		{
			return "@echo off\n"
			       + (CommandLineOptions.Brokerage.Length > 0 ? "set FASTBUILD_BROKERAGE_PATH=" + CommandLineOptions.Brokerage + "\n" : "")
			       + "%comspec% /c \"" + VCBasePath + "Auxiliary\\Build\\vcvarsall.bat\" " + (platform.ToLower() == "x64" ? "x64" : "x86") + " " + windowsSdkVersion;
		}

		protected static void GenFastBuildBatchFile(string filePath, string platform, string windowSdkVersion)
		{
			string content = GetBatchFileHeadText(platform, windowSdkVersion) + " && \"" + CommandLineOptions.FBExePath + "\" %*";

			var project = CurrentProject.Proj;
			List<string> properties = new List<string>() { "TargetFrameworkVersion", "PlatformToolSet", "EnableManagedIncrementalBuild", "VCToolArchitecture", "WindowsTargetPlatformVersion" };
			string line = "#" + string.Join(":", properties.ConvertAll(name => $"{name}={project.GetProperty(name).EvaluatedValue}"));
			string projectName = project.GetProperty("ProjectName").EvaluatedValue;
			string tlogPath = project.GetProperty("IntDir").EvaluatedValue + projectName + ".tlog";
			content += string.Format("\n\n@if not exist {0} mkdir {0}\n@echo {1}>{2}\n@echo on>>{2}\n@echo {3}^|{4}^|{5}^|>>{2}",
										tlogPath, line, Path.Combine(tlogPath, projectName + ".lastbuildstate"),
										CommandLineOptions.Config, CommandLineOptions.Platform, Path.GetDirectoryName(CommandLineOptions.Solution) + "\\");
#if NULL_FASTBUILD_OUTPUT
			BatchFileText += " > nul";
#endif
			File.WriteAllText(filePath, content);
        }

        public static Process GenWorkProcess(string ProjectPath, string Platform)
        {
            string projectDir = Path.GetDirectoryName(ProjectPath);
	        string batchFilePath = Path.Combine(projectDir, "fb.bat");
            
			GenFastBuildBatchFile(batchFilePath, Platform, WindowsSDKTarget);

	        Process fbProcess = new Process
	        {
		        StartInfo =
		        {
			        FileName = batchFilePath,
			        Arguments = "-config \"" + bffOutputFilePath + "\" " + CommandLineOptions.FBArgs,
			        RedirectStandardOutput = true,
			        UseShellExecute = false,
			        WorkingDirectory = projectDir,
			        StandardOutputEncoding = Console.OutputEncoding
		        }
	        };

	        return fbProcess;
        }

        public static bool ExecuteBffFile(string projectPath, string platform)
        {
            Console.WriteLine("Building " + Path.GetFileNameWithoutExtension(projectPath));

            try
            {
	            Process fbProcess = GenWorkProcess(projectPath, platform);

	            fbProcess.Start();
                while (!fbProcess.StandardOutput.EndOfStream)
                {
                    Console.Write(fbProcess.StandardOutput.ReadLine() + "\n");
                }
	            fbProcess.WaitForExit();
                return fbProcess.ExitCode == 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed to launch FASTBuild!");
                Console.WriteLine("Exception: " + e.Message);
                return false;
            }
        }

        public class ObjectListNode
		{
			readonly string _compiler;
			readonly string _outputPath;
			readonly string _options;
            readonly string _outputExtension;
            readonly string _pchText;
			readonly List<string> _inputFiles;
		
			public ObjectListNode(string inputFile, string compiler, string outputPath, string options, string pchText, string outputExtension = "")
			{
				_inputFiles = new List<string>();
				_inputFiles.Add(inputFile);
				_compiler = compiler;
				_outputPath = outputPath;
				_options = options;
				_outputExtension = outputExtension;
				_pchText = pchText;
			}
		
			public bool AddIfMatches(string inputFile, string compiler, string outputPath, string options, string pchText, string outputExtension = "")
			{
				if(_compiler == compiler && _outputPath == outputPath && _options == options && _pchText == pchText && _outputExtension == outputExtension)
				{
					_inputFiles.Add(inputFile);
					return true;
				}
				return false;
			}
		
			public string ToString(int id)
			{
				StringBuilder builder = new StringBuilder();

                bool usedUnity = false;
				if(CommandLineOptions.UseUnity && _compiler != "rc" && _inputFiles.Count > 1)
				{
					builder.AppendFormat("Unity('unity_{0}')\n{{\n", id);
					builder.AppendFormat("\t.UnityInputFiles = {{ {0} }}\n", string.Join(",", _inputFiles.ConvertAll(el => $"'{el}'")).ToArray());
					builder.AppendFormat("\t.UnityOutputPath = \"{0}\"\n", _outputPath);
					builder.AppendFormat("\t.UnityNumFiles = {0}\n", 1 + _inputFiles.Count / 10);
                    builder.Append("}\n\n");
					usedUnity = true;
				}

				builder.AppendFormat("ObjectList('action_{0}')\n{{\n", id);
				builder.AppendFormat("\t.Compiler = '{0}'\n", _compiler);
                builder.AppendFormat("\t.CompilerOutputPath = \"{0}\"\n", _outputPath);
				if(usedUnity)
				{
					builder.AppendFormat("\t.CompilerInputUnity = {{ 'unity_{0}' }}\n", id);
				}
				else
				{
					builder.AppendFormat("\t.CompilerInputFiles = {{ {0} }}\n", string.Join(",", _inputFiles.ConvertAll(el => $"'{el}'")));
				}
				builder.AppendFormat("\t.CompilerOptions = '{0}'\n", _options);
				if (!string.IsNullOrEmpty(_outputExtension))
				{
					builder.AppendFormat("\t.CompilerOutputExtension = '{0}'\n", _outputExtension);
				}
				if (!string.IsNullOrEmpty(_pchText))
				{
					builder.Append(_pchText);
				}
				if (!string.IsNullOrEmpty(PreBuildBatchFile))
				{
					builder.Append("\t.PreBuildDependencies  = 'prebuild'\n");
				}
				builder.Append("}\n\n");
				return builder.ToString();
			}
		}

		private static void AddExtraDlls(StringBuilder outputString, string rootDir, string pattern)
		{
			string[] dllFiles = Directory.GetFiles(rootDir, pattern);
			foreach (string dllFile in dllFiles)
			{
				outputString.AppendFormat("\t\t'$Root$/{0}'\n", Path.GetFileName(dllFile));
			}
		}

		public static BuildType GetProjectBuildType(Project project)
		{
			string configType = project.GetProperty("ConfigurationType").EvaluatedValue;
			switch (configType)
			{
				case "DynamicLibrary": return BuildType.DynamicLib;
				case "StaticLibrary": return BuildType.StaticLib;
				default: /*case "Application":*/ return BuildType.Application;
			}
        }

        private static void GenerateBffFromVcxproj(MSFBProject CurrentProject, string Config, string Platform)
        {
	        Project activeProject = CurrentProject.Proj;
			PreBuildBatchFile = "";
			bool hasFileChanged = HasFileChanged(activeProject.FullPath, Platform, Config, out var md5Hash);
			if (!hasFileChanged && !CommandLineOptions.AlwaysRegenerate)
				return; // Nothing changed, just return

			string windowsSdkBasePath = activeProject.GetProperty("WindowsSdkDir").EvaluatedValue;
			string OutDir = activeProject.GetProperty("OutDir").EvaluatedValue;
			string IntDir = activeProject.GetProperty("IntDir").EvaluatedValue;

			StringBuilder builder = new StringBuilder(md5Hash + "\n\n");

			builder.AppendFormat(".VSBasePath = '{0}'\n", activeProject.GetProperty("VSInstallDir").EvaluatedValue);
			builder.AppendFormat(".VCBasePath = '{0}'\n", VCBasePath);
			builder.AppendFormat(".VCExePath = '{0}'\n", VCExePath );

			builder.AppendFormat(".WindowsSDKBasePath = '{0}'\n\n", windowsSdkBasePath);
            // settings
			builder.AppendLine(GenProjectSettingItem(activeProject));
            // MSVC
			builder.AppendLine(GenMsvcCompilerItem());
            // Windows resource compiler
			builder.AppendLine(GenWindowRCItem(windowsSdkBasePath, WindowsSDKTarget, Platform));
            // prebuild event
			string preBuildItemText = CheckPreBuild(activeProject, Platform, WindowsSDKTarget);
            if (!string.IsNullOrEmpty(preBuildItemText))
				builder.Append(preBuildItemText);
            // -----------------------------------------------------------------
            // source code files
            string CompilerOptions = "";

			List<ObjectListNode> ObjectLists = new List<ObjectListNode>();
			var CompileItems = activeProject.GetItems("ClCompile");
			string PrecompiledHeaderString = "";

			foreach (var Item in CompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
					{
						ToolTask clTask = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
						clTask.GetType().GetProperty("Sources").SetValue(clTask, new TaskItem[] { new TaskItem() });
						string pchCompilerOptions = GenerateTaskCommandLine(clTask, new string[] { "PrecompiledHeaderOutputFile", "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
						PrecompiledHeaderString = "\t.PCHOptions = '" + string.Format("\"%1\" /Fp\"%2\" /Fo\"%3\" {0} '\n", pchCompilerOptions);
						PrecompiledHeaderString += "\t.PCHInputFile = '" + Item.EvaluatedInclude + "'\n";
						PrecompiledHeaderString += "\t.PCHOutputFile = '" + Item.GetMetadataValue("PrecompiledHeaderOutputFile") + "'\n";
						break; //Assumes only one pch...
					}
				}
			}

			foreach (var Item in CompileItems)
			{
				bool ExcludePrecompiledHeader = false;
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "Create").Any())
						continue;
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "PrecompiledHeader" && dmd.EvaluatedValue == "NotUsing").Any())
						ExcludePrecompiledHeader = true;
				}

				ToolTask Task = (ToolTask) Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.CL"));
				Task.GetType().GetProperty("Sources").SetValue(Task, new TaskItem[] { new TaskItem() }); //CPPTasks throws an exception otherwise...
				string TempCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ObjectFileName", "AssemblerListingLocation" }, Item.Metadata) + " /FS";
				if (Path.GetExtension(Item.EvaluatedInclude) == ".c")
					TempCompilerOptions += " /TC";
				else
					TempCompilerOptions += " /TP";
				CompilerOptions = TempCompilerOptions;
				string outDir = IntDir;
				string outExt = "";
				if (Item.DirectMetadataCount > 0)
				{
					ProjectMetadata element = Item.DirectMetadata.ElementAt(0);
					if (element.Name == "ObjectFileName")
					{
						outDir = Path.GetDirectoryName(element.EvaluatedValue);
						string name = Path.GetFileName(element.EvaluatedValue);
						outExt = name.Substring(name.IndexOf("."));
					}
				}
				string FormattedCompilerOptions = string.Format("\"%1\" /Fo\"%2\" {0}", TempCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "msvc", outDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString, outExt));
				if(!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "msvc", outDir, FormattedCompilerOptions, ExcludePrecompiledHeader ? "" : PrecompiledHeaderString, outExt));
				}
			}

			// -----------------------------------------------------------------
			// resource files
			PrecompiledHeaderString = "";

            var ResourceCompileItems = activeProject.GetItems("ResourceCompile");
			foreach (var Item in ResourceCompileItems)
			{
				if (Item.DirectMetadata.Any())
				{
					if (Item.DirectMetadata.Where(dmd => dmd.Name == "ExcludedFromBuild" && dmd.EvaluatedValue == "true").Any())
						continue;
				}
			
				ToolTask Task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.RC"));
				string ResourceCompilerOptions = GenerateTaskCommandLine(Task, new string[] { "ResourceOutputFileName", "DesigntimePreprocessorDefinitions" }, Item.Metadata);
			
				string formattedCompilerOptions = string.Format("{0} /fo\"%2\" \"%1\"", ResourceCompilerOptions);
				var MatchingNodes = ObjectLists.Where(el => el.AddIfMatches(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString));
				if (!MatchingNodes.Any())
				{
					ObjectLists.Add(new ObjectListNode(Item.EvaluatedInclude, "rc", IntDir, formattedCompilerOptions, PrecompiledHeaderString, ".res"));
				}
			}
			// -----------------------------------------------------------------
            int ActionNumber = 0;
			foreach (ObjectListNode ObjList in ObjectLists)
			{
				builder.Append(ObjList.ToString(ActionNumber));
				ActionNumber++;		
			}

			if (ActionNumber > 0)
			{
				HasCompileActions = true;
			}
			else
			{
				HasCompileActions = false;
				Console.WriteLine("Project has no actions to compile.");
			}

			string compileActions = string.Join(",", Enumerable.Range(0, ActionNumber).ToList().ConvertAll(i => $"'action_{i}'").ToArray());

			BuildType buildType = GetProjectBuildType(activeProject);
            if (buildType == BuildType.Application || buildType == BuildType.DynamicLib)
			{
				builder.AppendLine(GenExeOrDllItem(CurrentProject, buildType == BuildType.Application, compileActions, out var dependPath));

                if (HasCompileActions && buildType != BuildType.Application)
				{
					if (Path.IsPathRooted(dependPath))
						dependPath = dependPath.Replace('\\', '/');
					else
						dependPath = Path.Combine(activeProject.DirectoryPath, dependPath).Replace('\\', '/');

					foreach (var deps in CurrentProject.dependProjects)
					{
						deps.additionalLinkInputs += " \"" + dependPath + "\" ";
					}
				}
			}
			else if(buildType == BuildType.StaticLib)
            {
	            builder.AppendLine(GenStaticLibItem(CurrentProject, IntDir, CompilerOptions, compileActions, out var outputFile));
				if (HasCompileActions)
				{
					string dependencyPath = "";
					if (Path.IsPathRooted(outputFile))
						dependencyPath = Path.GetFullPath(outputFile).Replace('\\', '/');
					else
						dependencyPath = Path.Combine(CurrentProject.Proj.DirectoryPath, outputFile).Replace('\\', '/');

					foreach (var dep in CurrentProject.dependProjects)
						dep.additionalLinkInputs += " \"" + dependencyPath + "\" ";
				}
            }

            string postBuildItemText = CheckPostBuild(CurrentProject.Proj, Platform, WindowsSDKTarget);
			if (!string.IsNullOrEmpty(postBuildItemText))
				builder.Append(postBuildItemText);

			builder.AppendFormat("Alias ('all')\n{{\n\t.Targets = {{ '{0}' }}\n}}", string.IsNullOrEmpty(postBuildItemText) ? "output" : "postbuild");

			File.WriteAllText(bffOutputFilePath, builder.ToString());
		}

		protected static string GenProjectSettingItem(Project project)
		{
			StringBuilder builder = new StringBuilder("Settings\n{\n\t.Environment = \n\t{\n");
			builder.AppendFormat("\t\t\"INCLUDE={0}\",\n", project.GetProperty("IncludePath").EvaluatedValue);
			builder.AppendFormat("\t\t\"LIB={0}\",\n", project.GetProperty("LibraryPath").EvaluatedValue);
			builder.AppendFormat("\t\t\"LIBPATH={0}\",\n", project.GetProperty("ReferencePath").EvaluatedValue);
			builder.AppendFormat("\t\t\"PATH={0}\"\n", project.GetProperty("Path").EvaluatedValue);
			builder.AppendFormat("\t\t\"TMP={0}\"\n", project.GetProperty("Temp").EvaluatedValue);
			builder.AppendFormat("\t\t\"TEMP={0}\"\n", project.GetProperty("Temp").EvaluatedValue);
			builder.AppendFormat("\t\t\"SystemRoot={0}\"\n", project.GetProperty("SystemRoot").EvaluatedValue);
			builder.AppendLine("\t}\n}");
			return builder.ToString();
		}

		protected static string GenMsvcCompilerItem()
        {
            StringBuilder builder = new StringBuilder("Compiler('msvc')\n{\n");

            builder.AppendLine("\t.Root = '$VCExePath$'");
            builder.AppendLine("\t.Executable = '$Root$/cl.exe'");
            builder.AppendLine("\t.ExtraFiles =\n\t{");
            builder.AppendLine("\t\t'$Root$/c1.dll'");
            builder.AppendLine("\t\t'$Root$/c1xx.dll'");
            builder.AppendLine("\t\t'$Root$/c2.dll'");

            string compilerRoot = VCExePath;
            if (File.Exists(compilerRoot + "1033/clui.dll")) //Check English first...
            {
                builder.AppendLine("\t\t'$Root$/1033/clui.dll'");
            }
            else
            {
                var numericDirectories = Directory.GetDirectories(compilerRoot).Where(d => Path.GetFileName(d).All(char.IsDigit));
                var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
                if (cluiDirectories.Any())
                {
                    builder.AppendFormat("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First()));
                }
            }

            builder.AppendLine("\t\t'$Root$/mspdbsrv.exe'");
            //builder.AppendLine("\t\t'$Root$/mspdbcore.dll'");

            //builder.AppendFormat("\t\t'$Root$/mspft{0}.dll'\n", PlatformToolsetVersion);
            //builder.AppendFormat("\t\t'$Root$/msobj{0}.dll'\n", PlatformToolsetVersion);
            //builder.AppendFormat("\t\t'$Root$/mspdb{0}.dll'\n", PlatformToolsetVersion);
            //builder.AppendFormat("\t\t'$VSBasePath$/VC/redist/{0}/Microsoft.VC{1}.CRT/msvcp{1}.dll'\n", Platform == "Win32" ? "x86" : "x64", PlatformToolsetVersion);
            //builder.AppendFormat("\t\t'$VSBasePath$/VC/redist/{0}/Microsoft.VC{1}.CRT/vccorlib{1}.dll'\n", Platform == "Win32" ? "x86" : "x64", PlatformToolsetVersion);

            AddExtraDlls(builder, compilerRoot, "msobj*.dll");
            AddExtraDlls(builder, compilerRoot, "mspdb*.dll");
            AddExtraDlls(builder, compilerRoot, "mspft*.dll");
            AddExtraDlls(builder, compilerRoot, "msvcp*.dll");
            AddExtraDlls(builder, compilerRoot, "tbbmalloc.dll");
            AddExtraDlls(builder, compilerRoot, "vcmeta.dll");
            AddExtraDlls(builder, compilerRoot, "vcruntime*.dll");

            builder.AppendLine("\t}"); //End extra files
            builder.AppendLine("}"); //End compiler
            return builder.ToString();
        }

		protected static string GenWindowRCItem(string windowsSdkBasePath, string windowsSdkVersion, string platform)
		{
			platform = platform.ToLower() == "x64" ? "x64" : "x86";
            string rcPath = "\\bin\\" + windowsSdkVersion + "\\" + platform + "\\rc.exe";
			if (!File.Exists(windowsSdkBasePath + rcPath))
			{
				rcPath = "\\bin\\" + platform + "\\rc.exe";
			}
			StringBuilder resCompilerString = new StringBuilder("Compiler('rc')\n{\n");
			resCompilerString.AppendLine("\t.Executable = '$WindowsSDKBasePath$" + rcPath + "'");
			resCompilerString.AppendLine("\t.CompilerFamily = 'custom'");
			resCompilerString.AppendLine("}");

			return resCompilerString.ToString();
		}

		protected static string CheckPreBuild(Project project, string platform, string windowsSdkVersion)
		{
			if (!project.GetItems("PreBuildEvent").Any())
				return string.Empty;
			ProjectItem buildEvent = project.GetItems("PreBuildEvent").First();
			if (!buildEvent.Metadata.Any())
				return string.Empty;
			ProjectMetadata metaData = buildEvent.Metadata.First();
			if (string.IsNullOrEmpty(metaData.EvaluatedValue))
				return string.Empty;

			string headText = GetBatchFileHeadText(platform, windowsSdkVersion);
			string filePath = Path.Combine(project.DirectoryPath, Path.GetFileNameWithoutExtension(project.FullPath) + "_prebuild.bat");
			File.WriteAllText(filePath, headText + "\n" + metaData.EvaluatedValue);

			StringBuilder builder = new StringBuilder("Exec('prebuild') \n{\n");
			builder.AppendFormat("\t.ExecExecutable = '{0}' \n", filePath);
			builder.AppendFormat("\t.ExecInput = '{0}' \n", filePath);
			builder.AppendFormat("\t.ExecOutput = '{0}' \n", filePath + ".txt");
			builder.AppendLine("\t.ExecUseStdOutAsOutput = true");
			builder.AppendLine("}\n");

			return builder.ToString();
		}

        protected static string GenExeOrDllItem(MSFBProject project, bool isExe, string compileActions, out string dependPath)
		{
			StringBuilder builder = new StringBuilder();
			builder.AppendFormat("{0}('output')\n{{", isExe ? "Executable" : "DLL");
			builder.Append("\t.Linker = '$VCExePath$\\link.exe'\n");

			var linkDefinitions = project.Proj.ItemDefinitions["Link"];
			string outputFile = linkDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');

			dependPath = linkDefinitions.GetMetadataValue("ImportLibrary");

			ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.Link"));
			string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile", "ProfileGuidedDatabase" }, linkDefinitions.Metadata);
			if (!string.IsNullOrEmpty(project.additionalLinkInputs))
			{
				linkerOptions += project.additionalLinkInputs;
			}
			builder.AppendFormat("\t.LinkerOptions = '\"%1\" /OUT:\"%2\" {0}'\n", linkerOptions.Replace("'", "^'"));
			builder.AppendFormat("\t.LinkerOutput = '{0}'\n", outputFile);
			builder.AppendFormat("\t.Libraries = {{ {0} }}\n", compileActions);

			builder.AppendLine("}");
			return builder.ToString();
		}

        protected static string GenStaticLibItem(MSFBProject project, string outputPath, string compilerOptions, string compileActions, out string outputFile)
		{
			StringBuilder builder = new StringBuilder();
			builder.Append("Library('output')\n{");
			builder.Append("\t.Compiler = 'msvc'\n");
			builder.Append(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c {0}'\n", compilerOptions));
			builder.Append(string.Format("\t.CompilerOutputPath = \"{0}\"\n", outputPath));
			builder.Append("\t.Librarian = '$VCExePath$\\lib.exe'\n");

			var libDefinitions = project.Proj.ItemDefinitions["Lib"];
			outputFile = libDefinitions.GetMetadataValue("OutputFile").Replace('\\', '/');

			ToolTask task = (ToolTask)Activator.CreateInstance(CPPTasksAssembly.GetType("Microsoft.Build.CPPTasks.LIB"));
			string linkerOptions = GenerateTaskCommandLine(task, new string[] { "OutputFile" }, libDefinitions.Metadata);
			if (!string.IsNullOrEmpty(project.additionalLinkInputs))
			{
				linkerOptions += project.additionalLinkInputs;
			}
			builder.AppendFormat("\t.LibrarianOptions = '\"%1\" /OUT:\"%2\" {0}'\n", linkerOptions);
			builder.AppendFormat("\t.LibrarianOutput = '{0}'\n", outputFile);
			builder.AppendFormat("\t.LibrarianAdditionalInputs = {{ {0} }}\n", compileActions);

			builder.AppendLine("}");
			return builder.ToString();
		}

        protected static string CheckPostBuild(Project project, string platform, string windowsSdkVersion)
		{
			if (!project.GetItems("PostBuildEvent").Any())
				return string.Empty;
			ProjectItem buildEvent = project.GetItems("PostBuildEvent").First();
			if (!buildEvent.Metadata.Any())
				return string.Empty;
			ProjectMetadata metaData = buildEvent.Metadata.First();
			if (string.IsNullOrEmpty(metaData.EvaluatedValue))
				return string.Empty;

			string headText = GetBatchFileHeadText(platform, windowsSdkVersion);
            string filePath = Path.Combine(project.DirectoryPath, Path.GetFileNameWithoutExtension(project.FullPath) + "_postbuild.bat");
			File.WriteAllText(filePath, headText + "\n" + metaData.EvaluatedValue);

			StringBuilder builder = new StringBuilder("Exec('postbuild') \n{\n");
			builder.AppendFormat("\t.ExecExecutable = '{0}' \n", filePath);
			builder.AppendFormat("\t.ExecInput = '{0}' \n", filePath);
			builder.AppendFormat("\t.ExecOutput = '{0}' \n", filePath + ".txt");
			builder.Append("\t.PreBuildDependencies = 'output' \n");
			builder.Append("\t.ExecUseStdOutAsOutput = true \n");
            builder.Append("}\n\n");
	
			return builder.ToString();
		}

		public static string GenerateTaskCommandLine(ToolTask Task, string[] PropertiesToSkip, IEnumerable<ProjectMetadata> MetaDataList)
		{
			foreach (ProjectMetadata MetaData in MetaDataList)
			{
				if (PropertiesToSkip.Contains(MetaData.Name))
					continue;

				var MatchingProps = Task.GetType().GetProperties().Where(prop => prop.Name == MetaData.Name);
				if (MatchingProps.Any() && !string.IsNullOrEmpty(MetaData.EvaluatedValue))
				{
					string EvaluatedValue = MetaData.EvaluatedValue.Trim();
					if(MetaData.Name == "AdditionalIncludeDirectories")
					{
						EvaluatedValue = EvaluatedValue.Replace("\\\\", "\\");
						EvaluatedValue = EvaluatedValue.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
					}

					PropertyInfo propInfo = MatchingProps.First(); //Dubious
					if (propInfo.PropertyType.IsArray && propInfo.PropertyType.GetElementType() == typeof(string))
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue.Split(';'), propInfo.PropertyType));
					}
					else
					{
						propInfo.SetValue(Task, Convert.ChangeType(EvaluatedValue, propInfo.PropertyType));
					}
				}
			}

			var GenCmdLineMethod = Task.GetType().GetRuntimeMethods().Where(meth => meth.Name == "GenerateCommandLine").First(); //Dubious
			return GenCmdLineMethod.Invoke(Task, new object[] { Type.Missing, Type.Missing }) as string;
		}
	}

}
