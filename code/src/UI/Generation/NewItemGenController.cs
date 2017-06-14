﻿// ******************************************************************
// Copyright (c) Microsoft. All rights reserved.
// This code is licensed under the MIT License (MIT).
// THE CODE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH
// THE CODE OR THE USE OR OTHER DEALINGS IN THE CODE.
// ******************************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

using Microsoft.TemplateEngine.Edge.Template;
using Microsoft.Templates.Core;
using Microsoft.Templates.Core.Diagnostics;
using Microsoft.Templates.Core.Gen;
using Microsoft.Templates.Core.PostActions;
using Microsoft.Templates.Core.PostActions.Catalog.Merge;
using Microsoft.VisualStudio.TemplateWizard;
using Newtonsoft.Json;

namespace Microsoft.Templates.UI
{
    public class NewItemGenController : GenController
    {
        private static Lazy<NewItemGenController> _instance = new Lazy<NewItemGenController>(Initialize);
        public static NewItemGenController Instance => _instance.Value;


        private static NewItemGenController Initialize()
        {
            return new NewItemGenController(new NewItemPostActionFactory());
        }

        private NewItemGenController(PostActionFactory postactionFactory)
        {
            _postactionFactory = postactionFactory;
        }

        public (string ProjectType, string Framework) ReadProjectConfiguration()
        {
            //TODO: Review this
            var path = Path.Combine(GenContext.Current.ProjectPath, "Package.appxmanifest");
            if (File.Exists(path))
            {
                var manifest = XElement.Load(path);

                var metadata = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "Metadata");
                var projectType = metadata?.Descendants().FirstOrDefault(m => m.Attribute("Name").Value == "projectType")?.Attribute("Value")?.Value;
                var framework = metadata?.Descendants().FirstOrDefault(m => m.Attribute("Name").Value == "framework")?.Attribute("Value")?.Value;

                return (projectType, framework);
            }
            
            return (string.Empty, string.Empty);
        }

        public UserSelection GetUserSelectionNewItem(TemplateType templateType)
        {
            var newItem = new Views.NewItem.MainView(templateType);

            try
            {
                CleanStatusBar();

                GenContext.ToolBox.Shell.ShowModal(newItem);
                if (newItem.Result != null)
                {
                    //TODO: Review when right-click-actions available to track Project or Page completed.
                    //AppHealth.Current.Telemetry.TrackWizardCompletedAsync(WizardTypeEnum.NewItem).FireAndForget();

                    return newItem.Result;
                }
                else
                {
                    //TODO: Review when right-click-actions available to track Project or Page cancelled.
                    //AppHealth.Current.Telemetry.TrackWizardCancelledAsync(WizardTypeEnum.NewItem).FireAndForget();
                }

            }
            catch (Exception ex) when (!(ex is WizardBackoutException))
            {
                newItem.SafeClose();
                ShowError(ex);
            }

            GenContext.ToolBox.Shell.CancelWizard();

            return null;
        }

        public async Task GenerateNewItemAsync(UserSelection userSelection)
        {
            try
            {
               await UnsafeGenerateNewItemAsync(userSelection);
            }
            catch (Exception ex)
            {
                ShowError(ex, userSelection);

                GenContext.ToolBox.Shell.CancelWizard(false);
            }
        }

        public async Task UnsafeGenerateNewItemAsync(UserSelection userSelection)
        {
            var genItems = GenComposer.ComposeNewItem(userSelection).ToList();
            var chrono = Stopwatch.StartNew();

            var genResults = await GenerateItemsAsync(genItems);

            chrono.Stop();

            // TODO: Review New Item telemetry
            TrackTelemery(genItems, genResults, chrono.Elapsed.TotalSeconds, userSelection.ProjectType, userSelection.Framework);
        }

        public NewItemGenerationResult CompareOutputAndProject()
        {
            var result = new NewItemGenerationResult();
            var files = Directory
                .EnumerateFiles(GenContext.Current.OutputPath, "*", SearchOption.AllDirectories)
                .Where(f => !Regex.IsMatch(f, MergePostAction.PostactionRegex) && !Regex.IsMatch(f, MergePostAction.FailedPostactionRegex))
                .ToList();

            foreach (var file in files)
            {
                var destFilePath = file.Replace(GenContext.Current.OutputPath, GenContext.Current.ProjectPath);
                var fileName = file.Replace(GenContext.Current.OutputPath + Path.DirectorySeparatorChar, String.Empty);
                var fileInfo = new NewItemGenerationFileInfo(fileName, file, destFilePath);

                if (!File.Exists(destFilePath))
                {
                    result.NewFiles.Add(fileInfo);     
                }
                else
                {
                    if (GenContext.Current.MergeFilesFromProject.ContainsKey(fileName))
                    {
                        result.ModifiedFiles.Add(fileInfo);                       
                    }
                    else
                    {
                        result.ConflictingFiles.Add(fileInfo);
                    }
                }
            }
            return result;
        }

        public void SyncNewItem(UserSelection userSelection)
        {
            try
            {
                UnsafeSyncNewItem(userSelection);
            }
            catch (Exception ex)
            {
                ShowError(ex, userSelection);
                GenContext.ToolBox.Shell.CancelWizard(false);
            }
        }

        public void UnsafeSyncNewItem(UserSelection userSelection)
        {
            var result = CompareOutputAndProject();

            //BackupProjectFiles(result);
            CopyFilesToProject(result);
            GenerateSyncSummary(result);
            ExecuteFinishGenerationPostActions();
            //CleanupTempGeneration();
        }

        public void OutputNewItem(UserSelection userSelection)
        {
            try
            {
                UnsafeOutputNewItem(userSelection);
            }
            catch (Exception ex)
            {
                ShowError(ex, userSelection);
                GenContext.ToolBox.Shell.CancelWizard(false);
            }
        }

        public void UnsafeOutputNewItem(UserSelection userSelection)
        {
            var result = CompareOutputAndProject();
            GenerateOutputSummary(result);
            ExecuteFinishGenerationPostActions();
        }

        private void GenerateOutputSummary(NewItemGenerationResult result)
        {
            var fileName = Path.Combine(GenContext.Current.OutputPath, "Steps to include new item generation.md");
            File.WriteAllLines(fileName, new string[] { "# Steps to include new item generation", "You have to follow theese steps to include the new item into you project" });
            

            if (result.NewFiles.Any())
            {
                File.AppendAllLines(fileName, new string[] { $"## New files:", "Copy and add those files to your project:" });
                foreach (var newFile in result.NewFiles)
                {
                    File.AppendAllLines(fileName, GetLinkToLocalFile(newFile));
                }
            }
            File.AppendAllLines(fileName, new string[] { "## Modified files: " });
            File.AppendAllLines(fileName, new string[] { $"Apply theese changes in the following files: ", "" });

            foreach (var mergeFile in GenContext.Current.MergeFilesFromProject)
            {
                File.AppendAllLines(fileName, new string[] { $"### Changes in File '{mergeFile.Key}':", "" });
                foreach (var mergeInfo in mergeFile.Value)
                {
                    File.AppendAllLines(fileName, new string[] { mergeInfo.Intent, "" });
                    File.AppendAllLines(fileName, new string[] { $"```{mergeInfo.Format}", mergeInfo.PostActionCode, "```", "" });
                }
            }
            
            if (result.ConflictingFiles.Any())
            {
                File.AppendAllLines(fileName, new string[] { $"## Conflicting files:", "" });
                foreach (var conflictFile in result.ConflictingFiles)
                {
                    File.AppendAllLines(fileName, GetLinkToLocalFile(conflictFile));
                }
            }

            GenContext.Current.FilesToOpen.Add(fileName);
        }

        private void GenerateSyncSummary(NewItemGenerationResult result)
        {
            var fileName = Path.Combine(GenContext.Current.OutputPath, "GenerationSummary.md");
            File.WriteAllLines(fileName, new string[] { "# Generation summary", "The following changes have been incorporated in your project" });
            File.AppendAllLines(fileName, new string[] { "## Modified files: " });
            foreach (var mergeFile in GenContext.Current.MergeFilesFromProject)
            {
                var modifiedFile = result.ModifiedFiles.FirstOrDefault(m => m.Name == mergeFile.Key);

                File.AppendAllLines(fileName, new string[] { $"### Changes in File '{mergeFile.Key}':", "" });

                if (modifiedFile != null)
                {
                    File.AppendAllLines(fileName, new string[] { $"See the final result: [{modifiedFile.Name}]({Uri.EscapeUriString(modifiedFile.ProjectFilePath)})", "" });
                    File.AppendAllLines(fileName, new string[] { $"The following changes were applied:" });
                }
                else
                {
                    File.AppendAllLines(fileName, new string[] { $"The changes could not be applied, please apply the following changes manually:", "" });
                    //Postaction failed, show info
                }
                foreach (var mergeInfo in mergeFile.Value)
                {
                    File.AppendAllLines(fileName, new string[] { mergeInfo.Intent, "" });
                    File.AppendAllLines(fileName, new string[] { $"```{mergeInfo.Format}", mergeInfo.PostActionCode, "```", "" });
                }
            }
            if (result.NewFiles.Any())
            {
                File.AppendAllLines(fileName, new string[] { $"## New files:", "" });
                foreach (var newFile in result.NewFiles)
                {
                    File.AppendAllLines(fileName, GetLinkToProjectFile(newFile));
                }
            }
            if (result.ConflictingFiles.Any())
            {
                File.AppendAllLines(fileName, new string[] { $"## Conflicting files:", "" });
                foreach (var conflictFile in result.ConflictingFiles)
                {
                    File.AppendAllLines(fileName, GetLinkToProjectFile(conflictFile));
                }
            }

            GenContext.Current.FilesToOpen.Add(fileName);
        }

        private static string[] GetLinkToLocalFile(NewItemGenerationFileInfo fileInfo)
        {
            return new string[] { $"* [{fileInfo.Name}]({fileInfo.Name})" };
        }

        private static string[] GetLinkToProjectFile(NewItemGenerationFileInfo fileInfo)
        {
            return new string[] { $"* [{fileInfo.Name}]({Uri.EscapeUriString(fileInfo.ProjectFilePath)})" }; 
        }

        private void ExecuteFinishGenerationPostActions()
        {
            var postActions = _postactionFactory.FindFinishGenerationPostActions();

            foreach (var postAction in postActions)
            {
                postAction.Execute();
            }
        }

        private void BackupProjectFiles(NewItemGenerationResult result)
        {
            var projectGuid = GenContext.ToolBox.Shell.GetActiveProjectGuid();

            if (string.IsNullOrEmpty(projectGuid))
            {
                //TODO: Handle this 
                return;
            }

            var backupFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Configuration.Current.BackupFolderName,
                projectGuid);

            var fileName = Path.Combine(backupFolder, "backup.json");

            if (Directory.Exists(backupFolder))
            {
                //TODO: Change this to cleanup folder
                Fs.SafeDeleteDirectory(backupFolder);
            }

            Fs.EnsureFolder(backupFolder);

            File.WriteAllText(fileName, JsonConvert.SerializeObject(result));

            var modifiedFiles = result.ConflictingFiles.Concat(result.ModifiedFiles);

            foreach (var file in modifiedFiles)
            {
                var originalFile = file.ProjectFilePath;
                var backupFile = Path.Combine(backupFolder, file.Name);
                var destDirectory = Path.GetDirectoryName(backupFile);
               
                Fs.SafeCopyFile(originalFile, destDirectory, true);
            }
        }

        public void CleanupTempGeneration()
        {
            GenContext.Current.GenerationWarnings.Clear();
            GenContext.Current.MergeFilesFromProject.Clear();
            GenContext.Current.ProjectItems.Clear();
            var directory = GenContext.Current.OutputPath;
            try
            {
                if (directory.Contains(Path.GetTempPath()))
                {
                    Fs.SafeDeleteDirectory(directory);
                }
            }
            catch (Exception ex)
            {
                var msg = $"The folder {directory} can't be delete. Error: {ex.Message}";
                AppHealth.Current.Warning.TrackAsync(msg, ex).FireAndForget();
            }
        }

        //public CompareResult ShowLastActionResult()
        //{
        //    //var newItem = new Views.NewItem.NewItemView();
        //    var undoLastAction = new Views.NewItem.UndoLastActionView();

        //    try
        //    {
        //        CleanStatusBar();

        //        GenContext.ToolBox.Shell.ShowModal(undoLastAction);
        //        if (undoLastAction.Result != null)
        //        {
        //            //TODO: Review when right-click-actions available to track Project or Page completed.
        //            //AppHealth.Current.Telemetry.TrackWizardCompletedAsync(WizardTypeEnum.NewItem).FireAndForget();

        //            return undoLastAction.Result;
        //        }
        //        else
        //        {
        //            //TODO: Review when right-click-actions available to track Project or Page cancelled.
        //            //AppHealth.Current.Telemetry.TrackWizardCancelledAsync(WizardTypeEnum.NewItem).FireAndForget();
        //        }

        //    }
        //    catch (Exception ex) when (!(ex is WizardBackoutException))
        //    {
        //        undoLastAction.SafeClose();
        //        ShowError(ex);
        //    }

        //    GenContext.ToolBox.Shell.CancelWizard();

        //    return null;
        //}

        //public CompareResult GetLastActionInfo()
        //{
        //    var projectGuid = GenContext.ToolBox.Shell.GetActiveProjectGuid();

        //    if (string.IsNullOrEmpty(projectGuid))
        //    {
        //        //TODO: Handle this 
        //        return null;
        //    }

        //    var backupFolder = Path.Combine(
        //       Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        //       Configuration.Current.BackupFolderName,
        //       projectGuid);

        //    var fileName = Path.Combine(backupFolder, "backup.json");

        //    if (!Directory.Exists(backupFolder))
        //    {
        //        //TODO: Handle this
        //    }

        //    return JsonConvert.DeserializeObject<CompareResult>(File.ReadAllText(fileName));

        //}

        //public void UndoLastAction(CompareResult result)
        //{
        //    var projectGuid = GenContext.ToolBox.Shell.GetActiveProjectGuid();

        //    var backupFolder = Path.Combine(
        //       Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        //       Configuration.Current.BackupFolderName,
        //       projectGuid);

        //    var modifiedFiles = result.ConflictingFiles.Concat(result.ModifiedFiles);

        //    foreach (var file in modifiedFiles)
        //    {
        //        var originalFile = Path.Combine(GenContext.Current.ProjectPath, file);
        //        var backupFile = Path.Combine(backupFolder, file);

        //        File.Copy(backupFile, originalFile, true);
        //    }

        //    foreach (var file in result.NewFiles)
        //    {
        //        var projectFile = Path.Combine(GenContext.Current.ProjectPath, file);
        //        File.Delete(projectFile);
        //        //TODO:Remove file from project
        //    }
        //}

        private void CopyFilesToProject(NewItemGenerationResult result)
        {
            var modifiedFiles = result.ConflictingFiles.Concat(result.ModifiedFiles);

            foreach (var file in modifiedFiles)
            {
                var sourceFile = file.NewItemGenerationFilePath;
                var destFileName = file.ProjectFilePath;
                var destDirectory = Path.GetDirectoryName(destFileName);
                Fs.SafeCopyFile(sourceFile, destDirectory, true);
            }

            foreach (var file in result.NewFiles)
            {
                var sourceFile = file.NewItemGenerationFilePath;
                var destFileName = file.ProjectFilePath;
                var destDirectory = Path.GetDirectoryName(destFileName);
                Fs.SafeCopyFile(sourceFile, destDirectory, true);
            }
        }


        private static void TrackTelemery(IEnumerable<GenInfo> genItems, Dictionary<string, TemplateCreationResult> genResults, double timeSpent, string appProjectType, string appFx)
        {
            try
            {
                int pagesAdded = genItems.Where(t => t.Template.GetTemplateType() == TemplateType.Page).Count();
                int featuresAdded = genItems.Where(t => t.Template.GetTemplateType() == TemplateType.Feature).Count();

                foreach (var genInfo in genItems)
                {
                    if (genInfo.Template == null)
                    {
                        continue;
                    }

                    string resultsKey = $"{genInfo.Template.Identity}_{genInfo.Name}";

                    if (genInfo.Template.GetTemplateType() == TemplateType.Project)
                    {
                        AppHealth.Current.Telemetry.TrackProjectGenAsync(genInfo.Template, 
                            appProjectType, appFx, genResults[resultsKey], pagesAdded, featuresAdded, timeSpent).FireAndForget();
                    }
                    else
                    {
                        AppHealth.Current.Telemetry.TrackItemGenAsync(genInfo.Template, appProjectType, appFx, genResults[resultsKey]).FireAndForget();
                    }
                }
            }
            catch (Exception ex)
            {
                AppHealth.Current.Exception.TrackAsync(ex, "Exception tracking telemetry for Template Generation.").FireAndForget();
            }
        }
    }
}
