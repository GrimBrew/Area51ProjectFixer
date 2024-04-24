using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

namespace VSProjectFixer
{
    internal class Program
    {
        static void RemoveAttributeIfFound(XmlNode node, string attributeName)
        {
            for (int i = 0; i < node.Attributes.Count; i++)
            {
                if (node.Attributes[i].Name == attributeName)
                {
                    node.Attributes.RemoveAt(i);
                    break;
                }
            }
        }

        static void CreateOrUpdateAttribute(XmlNode node, string name, string value)
        {
            // Check if the attribute already exists.
            for (int i = 0; i < node.Attributes.Count; i++)
            {
                if (node.Attributes[i].Name == name)
                {
                    // Update the attribute value.
                    node.Attributes[i].Value = value;
                    return;
                }
            }

            // If we made it here the attribute doesn't exist.
            node.Attributes.Append(node.OwnerDocument.CreateAttribute(name));
            node.Attributes[name].Value = value;
        }

        static void FixFilesChildNode(XmlNode node, string configuration, HashSet<string> requiredObjectFiles, string baseDirectory, HashSet<string> filesFound)
        {
            // Check the node name and handle accordingly.
            switch (node.Name)
            {
                case "Filter":
                    {
                        // Recursively process child nodes.
                        for (int i = 0; i < node.ChildNodes.Count; i++)
                            FixFilesChildNode(node.ChildNodes[i], configuration, requiredObjectFiles, baseDirectory, filesFound);
                        break;
                    }
                case "File":
                    {
                        // Get the relative path of the file.
                        string relativePath = node.Attributes["RelativePath"].Value;

                        // Check if this is a required object file or not.
                        string objName = Path.GetFileNameWithoutExtension(relativePath) + ".obj";
                        bool includeInBuild = requiredObjectFiles.Contains(objName);
                        Debug.WriteLine($"{(includeInBuild == true ? "INCLUDE" : "EXCLUDE")} {relativePath}");

                        // Check if the file is missing from the source tree.
                        string fullFilePath = $"{baseDirectory}\\{relativePath}";
                        if (includeInBuild == true)
                        {
                            if (File.Exists(fullFilePath) == false)
                            {
                                Console.WriteLine($"MISSING FILE: {fullFilePath}");
                            }
                            else
                            {
                                filesFound.Add(objName);
                            }
                        }

                        // Find the file configuration node for 'Xbox Debug|Win32' if it exists.
                        bool found = false;
                        for (int i = 0; i < node.ChildNodes.Count; i++)
                        {
                            // Check if this is the node we're looking for.
                            if (node.ChildNodes[i].Attributes["Name"].Value == $"Xbox {configuration}|Win32")
                            {
                                // Change the tool name attribute to match the new configuration name.
                                node.ChildNodes[i].Attributes["Name"].Value = $"{configuration}|Xbox";

                                // Remove junk attributes on the child Tool node.
                                XmlNode toolNode = node.ChildNodes[i].ChildNodes[0];
                                Debug.Assert(toolNode.Name == "Tool");

                                //RemoveAttributeIfFound(toolNode, "BasicRuntimeChecks");
                                //RemoveAttributeIfFound(toolNode, "BrowseInformation");

                                // Make sure the file is included/excluded correctly.
                                if (includeInBuild == true)
                                {
                                    // Remove any existing exclude attribute.
                                    RemoveAttributeIfFound(node.ChildNodes[i], "ExcludedFromBuild");
                                }
                                else
                                {
                                    // Check if the exclude from build attribute already exists.
                                    if (node.ChildNodes[i].Attributes["ExcludedFromBuild"] == null)
                                        node.ChildNodes[i].Attributes.Append(node.OwnerDocument.CreateAttribute("ExcludedFromBuild"));

                                    node.ChildNodes[i].Attributes["ExcludedFromBuild"].Value = "TRUE";
                                }
                                
                                found = true;
                                break;
                            }
                        }

                        // If a node for the configuration was not found add a new FileConfiguration node.
                        if (found == false)
                        {
                            // Only add file configuration nodes for header files if they are excluded from the build.
                            bool isHeader = relativePath.EndsWith(".hpp");
                            if (isHeader == false || (isHeader == true && includeInBuild == false))
                            {
                                // Create a new FileConfiguration node.
                                XmlNode fileConfigNode = node.OwnerDocument.CreateNode(XmlNodeType.Element, "FileConfiguration", "");
                                fileConfigNode.Attributes.Append(node.OwnerDocument.CreateAttribute("Name"));
                                fileConfigNode.Attributes["Name"].Value = $"{configuration}|Xbox";

                                if (includeInBuild == false)
                                {
                                    fileConfigNode.Attributes.Append(node.OwnerDocument.CreateAttribute("ExcludedFromBuild"));
                                    fileConfigNode.Attributes["ExcludedFromBuild"].Value = "TRUE";
                                }

                                fileConfigNode.AppendChild(node.OwnerDocument.CreateNode(XmlNodeType.Element, "Tool", ""));
                                fileConfigNode.ChildNodes[0].Attributes.Append(node.OwnerDocument.CreateAttribute("Name"));
                                fileConfigNode.ChildNodes[0].Attributes["Name"].Value = "VCCLCompilerTool";

                                node.AppendChild(fileConfigNode);
                            }
                        }
                        break;
                    }
            }
        }

        static XmlNode FindFilterNode(XmlNode parent, string path, bool create = true)
        {
            // Find a child name with matching name attribute.
            XmlNode child = null;
            for (int i = 0; i < parent.ChildNodes.Count; i++)
            {
                if (parent.ChildNodes[i].Attributes.GetNamedItem("Name")?.InnerText == path)
                {
                    child = parent.ChildNodes[i];
                    break;
                }
            }

            // If the child node wasn't found create it.
            if (child == null && create == true)
            {
                child = parent.OwnerDocument.CreateNode(XmlNodeType.Element, "Filter", "");
                child.Attributes.Append(child.OwnerDocument.CreateAttribute("Name"));
                child.Attributes[0].Value = path;
                child.Attributes.Append(child.OwnerDocument.CreateAttribute("Filter"));

                parent.AppendChild(child);
            }

            return child;
        }

        static void AddFileReference(XmlNode filesNode, string filterPath, string filePath, string configuration, string altObjectFileName = null)
        {
            // Split the filter path into parts.
            string[] filterParts = filterPath.Split("\\", StringSplitOptions.RemoveEmptyEntries);

            // Find the parent node for the file and create any missing nodes in the path.
            XmlNode filterNode = filesNode;
            for (int i = 0; i < filterParts.Length; i++)
            {
                filterNode = FindFilterNode(filterNode, filterParts[i]);
            }

            // Check if the file node for this file exists.
            XmlNode fileNode = null;
            for (int i = 0; i < filterNode.ChildNodes.Count; i++)
            {
                if (filterNode.ChildNodes[i].Name == "File" && filterNode.ChildNodes[i].Attributes["RelativePath"].Value == filePath)
                {
                    fileNode = filterNode.ChildNodes[i];
                    break;
                }
            }

            if (fileNode == null)
            {
                // Create a new File node.
                fileNode = filterNode.OwnerDocument.CreateNode(XmlNodeType.Element, "File", "");
                filterNode.AppendChild(fileNode);
                fileNode.Attributes.Append(fileNode.OwnerDocument.CreateAttribute("RelativePath"));
                fileNode.Attributes[0].Value = filePath;

                filterNode.AppendChild(fileNode);
            }

            // If this is a header file don't add configuration details.
            if (filePath.EndsWith(".hpp") == true)
                return;

            // Check if a node for this build configuration exists.
            XmlNode fileConfigNode = null;
            for (int i = 0; i < fileNode.ChildNodes.Count; i++)
            {
                if (fileNode.ChildNodes[i].Attributes["Name"].Value == $"{configuration}|Xbox")
                {
                    fileConfigNode = fileNode.ChildNodes[i];
                    break;
                }
            }

            if (fileConfigNode == null)
            {
                // Create a new FileConfiguration node.
                fileConfigNode = fileNode.OwnerDocument.CreateNode(XmlNodeType.Element, "FileConfiguration", "");
                fileConfigNode.Attributes.Append(fileNode.OwnerDocument.CreateAttribute("Name"));
                fileConfigNode.Attributes["Name"].Value = $"{configuration}|Xbox";

                fileConfigNode.AppendChild(fileNode.OwnerDocument.CreateNode(XmlNodeType.Element, "Tool", ""));
                fileConfigNode.ChildNodes[0].Attributes.Append(fileNode.OwnerDocument.CreateAttribute("Name"));
                fileConfigNode.ChildNodes[0].Attributes["Name"].Value = "VCCLCompilerTool";

                fileNode.AppendChild(fileConfigNode);
            }

            // Remove the exclude from build attribute if it exists.
            RemoveAttributeIfFound(fileConfigNode, "ExcludedFromBuild");

            // Check if the alternate object file name was specified.
            if (altObjectFileName != null)
            {
                XmlAttribute objFileAttr = fileConfigNode.OwnerDocument.CreateAttribute("ObjectFile");
                objFileAttr.Value = altObjectFileName;
                fileConfigNode.ChildNodes[0].Attributes.Append(objFileAttr);
            }
        }

        static void ExcludeFileReference(XmlNode filesNode, string filterPath, string filePath, string configuration)
        {
            // Split the filter path into parts.
            string[] filterParts = filterPath.Split("\\", StringSplitOptions.RemoveEmptyEntries);

            // Find the parent node for the file and create any missing nodes in the path.
            XmlNode filterNode = filesNode;
            for (int i = 0; i < filterParts.Length; i++)
            {
                filterNode = FindFilterNode(filterNode, filterParts[i], false);
            }

            // Find the file configuration node for 'Xbox Debug|Win32' if it exists.
            bool found = false;
            for (int i = 0; i < filterNode.ChildNodes.Count; i++)
            {
                // Check if this is the node we're looking for.
                if (filterNode.ChildNodes[i].Attributes["RelativePath"].Value == filePath)
                {
                    for (int x = 0; x < filterNode.ChildNodes[i].ChildNodes.Count; x++)
                    {
                        if (filterNode.ChildNodes[i].ChildNodes[x].Attributes["Name"].Value == $"{configuration}|Xbox")
                        {
                            // Check if the exclude from build attribute already exists.
                            if (filterNode.ChildNodes[i].ChildNodes[x].Attributes["ExcludedFromBuild"] == null)
                                filterNode.ChildNodes[i].ChildNodes[x].Attributes.Append(filterNode.OwnerDocument.CreateAttribute("ExcludedFromBuild"));

                            filterNode.ChildNodes[i].ChildNodes[x].Attributes["ExcludedFromBuild"].Value = "TRUE";

                            found = true;
                        }
                    }

                    // If the file configuration node wasn't found create one.
                    if (found == false)
                    {
                        // Create a new FileConfiguration node.
                        XmlNode fileConfigNode = filterNode.ChildNodes[i].OwnerDocument.CreateNode(XmlNodeType.Element, "FileConfiguration", "");
                        fileConfigNode.Attributes.Append(filterNode.ChildNodes[i].OwnerDocument.CreateAttribute("Name"));
                        fileConfigNode.Attributes["Name"].Value = $"{configuration}|Xbox";

                        fileConfigNode.Attributes.Append(filterNode.ChildNodes[i].OwnerDocument.CreateAttribute("ExcludedFromBuild"));
                        fileConfigNode.Attributes["ExcludedFromBuild"].Value = "TRUE";

                        fileConfigNode.AppendChild(filterNode.ChildNodes[i].OwnerDocument.CreateNode(XmlNodeType.Element, "Tool", ""));
                        fileConfigNode.ChildNodes[0].Attributes.Append(filterNode.ChildNodes[i].OwnerDocument.CreateAttribute("Name"));
                        fileConfigNode.ChildNodes[0].Attributes["Name"].Value = "VCCLCompilerTool";

                        filterNode.ChildNodes[i].AppendChild(fileConfigNode);
                        found = true;
                    }
                    break;
                }
            }

            Debug.Assert(found == true);
        }

        static void ProcessProjectFileConfiguration(string originalFile, string outputFile, XmlDocument doc, string configuration, Dictionary<string, HashSet<string>> LibObjDictionary)
        {
            string libFileName = Path.GetFileNameWithoutExtension(outputFile);

            // Step 2: Fixup the xbox debug configuration.
            bool configNodeFound = false;
            XmlNodeList configurationNodes = doc.SelectNodes("/VisualStudioProject/Configurations")[0].ChildNodes;
            for (int i = 0; i < configurationNodes.Count; i++)
            {
                // Check if this is the xbox debug node and if not skip it.
                if (configurationNodes[i].Attributes["Name"].Value != $"Xbox {configuration}|Win32")
                    continue;

                configNodeFound = true;

                // Fix the configuration name.
                configurationNodes[i].Attributes["Name"].Value = $"{configuration}|Xbox";

                // Update object directories.
                configurationNodes[i].Attributes["OutputDirectory"].Value = "$(ProjectDir)_$(PlatformName)$(ConfigurationName)";
                configurationNodes[i].Attributes["IntermediateDirectory"].Value = "$(ProjectDir)_$(PlatformName)$(ConfigurationName)";

                // Remove junk attributes.
                XmlNode xboxConfigNode = configurationNodes[i];
                RemoveAttributeIfFound(xboxConfigNode, "UseOfMFC");
                RemoveAttributeIfFound(xboxConfigNode, "ATLMinimizesCRunTimeLibraryUsage");

                // Remove junk tool nodes.
                List<XmlNode> childrenToRemove = new List<XmlNode>();
                for (int x = 0; x < xboxConfigNode.ChildNodes.Count; x++)
                {
                    // Check the tool name and handle accordingly.
                    switch (xboxConfigNode.ChildNodes[x].Attributes["Name"].Value)
                    {
                        case "VCCLCompilerTool":
                            {
                                // Update attributes for the configuration.
                                //UpdateCompilerConfigurationAttributes(xboxConfigNode.ChildNodes[x], configuration);

                                // Fixup preprocessor definitions attribute.
                                string preprocessor = xboxConfigNode.ChildNodes[x].Attributes["PreprocessorDefinitions"].Value;
                                string[] defines = preprocessor.Split(';').Except(new string[] { "X_DEBUG", "X_ASSERT", "$(USERNAME)" }).ToArray();

                                preprocessor = string.Join(',', defines).Replace("TARGET_XBOX_DEV", "TARGET_XBOX") + $",CONFIG_{configuration.ToUpper()}";

                                xboxConfigNode.ChildNodes[x].Attributes["PreprocessorDefinitions"].Value = preprocessor;

                                // Update includes for specific project files.
                                if (libFileName == "Music_mgr")
                                {
                                    xboxConfigNode.ChildNodes[x].Attributes["AdditionalIncludeDirectories"].Value += ",$(X)\\Auxiliary";
                                }
                                else if (libFileName == "fx_RunTime")
                                {
                                    xboxConfigNode.ChildNodes[x].Attributes["AdditionalIncludeDirectories"].Value += ",$(X)\\..\\Support";
                                }
                                else if (libFileName == "Entropy")
                                {
                                    xboxConfigNode.ChildNodes[x].Attributes["AdditionalIncludeDirectories"].Value += ",$(X)\\Entropy\\Audio";
                                }
                                break;
                            }
                        case "VCCustomBuildTool":
                            break;
                        case "VCLibrarianTool":
                            {
                                // Reset output directory.
                                RemoveAttributeIfFound(xboxConfigNode.ChildNodes[x], "OutputFile");
                                break;
                            }
                        case "VCPostBuildEventTool":
                        case "VCPreBuildEventTool":
                        case "VCPreLinkEventTool":
                        case "XboxDeploymentTool":
                        case "XboxImageTool":
                            {
                                // Keep the node.
                                break;
                            }
                        default:
                            {
                                // Mark the node for removal.
                                childrenToRemove.Add(xboxConfigNode.ChildNodes[x]);
                                break;
                            }
                    }
                }

                foreach (XmlNode child in childrenToRemove)
                    xboxConfigNode.RemoveChild(child);
            }

            // Make sure we found the node.
            if (configNodeFound == false)
            {
                void CreateAttribute(XmlNode node, string name, string value)
                {
                    node.Attributes.Append(node.OwnerDocument.CreateAttribute(name));
                    node.Attributes[name].Value = value;
                }

                // Manually build the configuration node.
                XmlNode configNode = doc.CreateNode(XmlNodeType.Element, "Configuration", "");
                CreateAttribute(configNode, "Name", $"{configuration}|Xbox");
                CreateAttribute(configNode, "OutputDirectory", "$(ProjectDir)_$(PlatformName)$(ConfigurationName)");
                CreateAttribute(configNode, "IntermediateDirectory", "$(ProjectDir)_$(PlatformName)$(ConfigurationName)");
                CreateAttribute(configNode, "ConfigurationType", "4");              // Shared library (.lib)

                XmlNode compilerNode = doc.CreateNode(XmlNodeType.Element, "Tool", "");
                CreateAttribute(compilerNode, "Name", "VCCLCompilerTool");
                //CreateAttribute(compilerNode, "Optimization", "0");
                CreateAttribute(compilerNode, "AdditionalIncludeDirectories", "$(X),$(X)\\..\\Support,$(X)\\Entropy,$(X)\\x_files,$(X)\\Auxiliary");
                CreateAttribute(compilerNode, "PreprocessorDefinitions", $"TARGET_XBOX,CONFIG_{configuration.ToUpper()}");
                if (libFileName != "Gamelib")
                {
                    //CreateAttribute(compilerNode, "BasicRuntimeChecks", "3");
                    //CreateAttribute(compilerNode, "RuntimeLibrary", "5");
                }
                //CreateAttribute(compilerNode, "BrowseInformation", "1");
                //CreateAttribute(compilerNode, "WarningLevel", "3");
                //CreateAttribute(compilerNode, "DebugInformationFormat", "4");

                // Update attributes for the configuration.
                //UpdateCompilerConfigurationAttributes(compilerNode, configuration);

                if (libFileName == "UserInterface" || libFileName == "Gamelib")
                {
                    compilerNode.Attributes["AdditionalIncludeDirectories"].Value += ",$(X)\\..\\xCore\\3rdParty\\BinkXbox\\Include";
                }
                else if (libFileName == "Entropy")
                {
                    compilerNode.Attributes["AdditionalIncludeDirectories"].Value += ",$(X)\\Entropy\\Audio";
                }

                XmlNode VCCustomBuildTool = doc.CreateNode(XmlNodeType.Element, "Tool", "");
                CreateAttribute(VCCustomBuildTool, "Name", "VCCustomBuildTool");

                XmlNode VCLibrarianTool = doc.CreateNode(XmlNodeType.Element, "Tool", "");
                CreateAttribute(VCLibrarianTool, "Name", "VCLibrarianTool");
                //CreateAttribute(VCLibrarianTool, "OutputFile", $".\\{configuration}\\{libFileName}.lib");

                XmlNode VCPostBuildEventTool = doc.CreateNode(XmlNodeType.Element, "Tool", "");
                CreateAttribute(VCPostBuildEventTool, "Name", "VCPostBuildEventTool");

                XmlNode VCPreBuildEventTool = doc.CreateNode(XmlNodeType.Element, "Tool", "");
                CreateAttribute(VCPreBuildEventTool, "Name", "VCPreBuildEventTool");

                XmlNode VCPreLinkEventTool = doc.CreateNode(XmlNodeType.Element, "Tool", "");
                CreateAttribute(VCPreLinkEventTool, "Name", "VCPreLinkEventTool");

                configNode.AppendChild(compilerNode);
                configNode.AppendChild(VCCustomBuildTool);
                configNode.AppendChild(VCLibrarianTool);
                configNode.AppendChild(VCPostBuildEventTool);
                configNode.AppendChild(VCPreBuildEventTool);
                configNode.AppendChild(VCPreLinkEventTool);

                doc.SelectNodes("/VisualStudioProject/Configurations")[0].AppendChild(configNode);
            }

            HashSet<string> filesFound = new HashSet<string>();

            // Get the list of required object files for this project.
            string libName = Path.GetFileNameWithoutExtension(outputFile);
            HashSet<string> requiredObjectFiles = LibObjDictionary[libName];

            string baseDirectory = Path.GetDirectoryName(outputFile);

            // Step 3: Fixup file references.
            XmlNode filesNode = doc.SelectSingleNode("/VisualStudioProject/Files");
            for (int i = 0; i < filesNode.ChildNodes.Count; i++)
            {
                FixFilesChildNode(filesNode.ChildNodes[i], configuration, requiredObjectFiles, baseDirectory, filesFound);
            }

            void ReferenceFile(string filterPath, string filePath, string altObjFileName = null)
            {
                AddFileReference(filesNode, filterPath, filePath, configuration, altObjFileName);

                string objName = Path.GetFileNameWithoutExtension(filePath) + ".obj";
                filesFound.Add(objName);
            }

            // Check the library name and add missing files to the project.
            if (libFileName == "Entropy")
            {
                ReferenceFile("Audio", "Audio\\audio_stream_controller.cpp");
                ReferenceFile("Audio", "Audio\\audio_stream_controller.hpp");

                ReferenceFile("Implementation - XBOX", "Xbox\\Xbox.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\Xbox_input.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\TextureMgr.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\TextureMgr.hpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\PushMgr.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\PushMgr.hpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\VertexMgr.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\VertexMgr.hpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\Xbox_draw.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\Xbox_vram.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\xbox_font.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\QuikHeap.cpp");
                ReferenceFile("Implementation - XBOX", "Xbox\\QuikHeap.h");

                ReferenceFile("IOManager", "IOManager\\device_net\\io_device_net.cpp");
                ReferenceFile("IOManager", "IOManager\\device_net\\io_device_net.hpp");

                ReferenceFile("MemCard", "MemCard\\memcard.cpp");
                ReferenceFile("MemCard", "MemCard\\memcard_xbox.cpp");

                ReferenceFile("Network\\XBOX", "Network\\XBOX\\NetLib.cpp");

                ExcludeFileReference(filesNode, "Network\\PC", "Network\\PC\\NetLib.cpp", configuration);
            }
            else if (libFileName == "x_files")
            {
                ReferenceFile("", "x_locale.hpp");
                ReferenceFile("Implementation", "Implementation\\x_locale.cpp");

                ReferenceFile("Implementation", "Implementation\\x_threads_xbox.cpp");

                // Exclude x_debug for non-debug configurations.
                if (configuration == "Retail")
                {
                    ExcludeFileReference(filesNode, "Implementation", "Implementation\\x_debug.cpp", configuration);
                }
            }
            else if (libFileName == "NetworkMgr")
            {
                ReferenceFile("Connection", "MatchMgr_Common.cpp");
                ReferenceFile("Connection", "MatchMgr_XBOX.cpp");

                ReferenceFile("VoiceMgr", "Voice\\headset_common.cpp");

                ReferenceFile("NetObjects", "..\\Objects\\Actor\\ActorNet.cpp");
                ReferenceFile("NetObjects", "..\\Objects\\NetGhost.cpp");
                ReferenceFile("NetObjects", "..\\Objects\\NetGhost.hpp");

                ReferenceFile("GameMgr", "logic_TDM.cpp");
                ReferenceFile("GameMgr", "logic_TDM.hpp");
                ReferenceFile("GameMgr", "logic_CTF.cpp");
                ReferenceFile("GameMgr", "logic_CTF.hpp");
                ReferenceFile("GameMgr", "logic_Tag.cpp");
                ReferenceFile("GameMgr", "logic_Tag.hpp");
                ReferenceFile("GameMgr", "logic_Infect.cpp");
                ReferenceFile("GameMgr", "logic_Infect.hpp");
                ReferenceFile("GameMgr", "logic_CNH.cpp");
                ReferenceFile("GameMgr", "logic_CNH.hpp");

                ReferenceFile("GameMgr", "PainQueue.cpp");
                ReferenceFile("GameMgr", "PainQueue.hpp");
                ReferenceFile("GameMgr", "MsqQueue.cpp");
                ReferenceFile("GameMgr", "MsgQueue.hpp");
                ReferenceFile("GameMgr", "MsgClient.cpp");
                ReferenceFile("GameMgr", "MsgClient.hpp");
                ReferenceFile("GameMgr", "Messages.cpp");
                ReferenceFile("GameMgr", "Messages.hpp");
                ReferenceFile("GameMgr", "Msg.cpp");
                ReferenceFile("GameMgr", "Msg.hpp");

                ReferenceFile("", "downloader\\archive.cpp");
                ReferenceFile("", "downloader\\archive.hpp");

                ReferenceFile("External", "downloader\\zlib\\inflate.c");
                ReferenceFile("External", "downloader\\zlib\\inflate.h");
                ReferenceFile("External", "downloader\\zlib\\zutil.c");
                ReferenceFile("External", "downloader\\zlib\\zutil.h");
                ReferenceFile("External", "downloader\\zlib\\inffast.c");
                ReferenceFile("External", "downloader\\zlib\\inffast.h");
                ReferenceFile("External", "downloader\\zlib\\inftrees.c");
                ReferenceFile("External", "downloader\\zlib\\inftrees.h");
                ReferenceFile("External", "downloader\\zlib\\adler32.c");
                ReferenceFile("External", "downloader\\zlib\\crc32.c");
                ReferenceFile("External", "downloader\\zlib\\crc32.h");
            }
            else if (libFileName == "Render")
            {
                ReferenceFile("XBOX", "Xbox\\LeastSquares.cpp");
                ReferenceFile("XBOX", "Xbox\\LeastSquares.hpp");
            }
            else if (libFileName == "Gamelib")
            {
                ReferenceFile("", "RenderContext.cpp");
                ReferenceFile("", "RenderContext.hpp");
                ReferenceFile("", "LevelLoader.cpp");
                ReferenceFile("", "LevelLoader.hpp");
                ReferenceFile("", "DebugCheats.cpp");
                ReferenceFile("", "DebugCheats.hpp");

                ReferenceFile("Actor\\Characters", "..\\Characters\\ActorEffects.cpp");
                ReferenceFile("Actor\\Characters", "..\\Characters\\ActorEffects.hpp");

                ExcludeFileReference(filesNode, "Actor\\Characters", "..\\Characters\\Character.hpp", configuration);
                ReferenceFile("Actor\\Characters\\General", "..\\Characters\\Character.hpp");

                ExcludeFileReference(filesNode, "Actor\\Characters", "..\\Characters\\God.hpp", configuration);
                ReferenceFile("Actor\\Characters\\General", "..\\Characters\\God.hpp");

                ReferenceFile("Actor\\Characters\\General\\Base Character States", "..\\Characters\\BaseStates\\Character_Alarm_State.cpp");
                ReferenceFile("Actor\\Characters\\General\\Base Character States", "..\\Characters\\BaseStates\\Character_Alarm_State.hpp");

                ReferenceFile("Actor\\Characters\\Grunt", "..\\Characters\\Grunt\\Grunt_Cover_State.cpp");
                ReferenceFile("Actor\\Characters\\Grunt", "..\\Characters\\Grunt\\Grunt_Cover_State.hpp");
                ReferenceFile("Actor\\Characters\\Grunt", "..\\Characters\\Grunt\\Leaper_Attack_State.cpp");
                ReferenceFile("Actor\\Characters\\Grunt", "..\\Characters\\Grunt\\Leaper_Attack_State.hpp");

                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\Soldier.cpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\Soldier.hpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\SoldierLoco.cpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\SoldierLoco.hpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\Soldier_Attack_State.cpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\Soldier_Attack_State.hpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\BlackOp_Cover_State.cpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\BlackOp_Cover_State.hpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\BlackOp_Attack_State.cpp");
                ReferenceFile("Actor\\Characters\\Soldiers", "..\\Characters\\Soldiers\\BlackOp_Attack_State.hpp");

                ReferenceFile("Animation", "..\\Animation\\BasePlayer.cpp");
                ReferenceFile("Animation", "..\\Animation\\AnimAudioTimer.cpp");
                ReferenceFile("Animation", "..\\Animation\\AnimAudioTimer.hpp");

                ReferenceFile("CheckPointMgr", "..\\CheckPointMgr\\CheckPointMgr.cpp");
                ReferenceFile("CheckPointMgr", "..\\CheckPointMgr\\CheckPointMgr.hpp");

                ReferenceFile("Configuration", "..\\Configuration\\GameConfig.cpp");
                ReferenceFile("Configuration", "..\\Configuration\\GameConfig.hpp");

                ReferenceFile("DataVault", "..\\DataVault\\DataVault.cpp");
                ReferenceFile("DataVault", "..\\DataVault\\DataVault.hpp");

                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenu2.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenu2.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageLocalization.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageLocalization.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageMultiplayer.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageMultiplayer.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageAdvCheckpoints.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageAdvCheckpoints.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPagePolyCache.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPagePolyCache.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPerception.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPerception.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuAudio.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuAudio.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageLogging.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageLogging.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageMonkey.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageMonkey.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageAIScript.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageAIScript.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageAimAssist.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageAimAssist.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageFx.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageFx.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageRender.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageRender.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageMemory.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageMemory.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageGameplay.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageGameplay.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageGeneral.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPageGeneral.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPage.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuPage.hpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuItem.cpp");
                ReferenceFile("DebugMenu", "..\\Menu\\DebugMenuItem.hpp");

                ReferenceFile("HUd", "..\\Objects\\hud_Player.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Player.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Text.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Text.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Health.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Health.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_MutantVision.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_MutantVision.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_ContagiousVision.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_ContagiousVision.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Damage.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Damage.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Sniper.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Sniper.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Ammo.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Ammo.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Reticle.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Reticle.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Icon.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Icon.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_InfoBox.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_InfoBox.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Vote.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Vote.hpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Scanner.cpp");
                ReferenceFile("HUd", "..\\Objects\\hud_Scanner.hpp");

                ReferenceFile("Inventory", "..\\Inventory\\Inventory2.cpp");
                ReferenceFile("Inventory", "..\\Inventory\\Inventory2.hpp");

                ReferenceFile("Loco", "..\\Loco\\LocoIKSolver.cpp");
                ReferenceFile("Loco", "..\\Loco\\LocoIKSolver.hpp");
                ReferenceFile("Loco", "..\\Loco\\LocoWheelController.cpp");
                ReferenceFile("Loco", "..\\Loco\\LocoWheelController.hpp");

                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_BootCheck.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_CreateProfile.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_DeleteContent.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_DeleteProfile.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_Format.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_LoadContent.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_LoadProfile.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_LoadSettings.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_PollCards.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_PollContent.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_SaveContent.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_SaveProfile.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\Action_SaveSettings.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\MemCardMgr.cpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\MemCardMgr.hpp");
                ReferenceFile("MemCardMgr", "..\\MemCardMgr\\MemCardMgrDialog.cpp");

                ReferenceFile("MoviePlayer", "..\\MoviePlayer\\MoviePlayer.cpp");
                ReferenceFile("MoviePlayer", "..\\MoviePlayer\\MoviePlayer.hpp");
                ReferenceFile("MoviePlayer", "..\\MoviePlayer\\MoviePlayer_Bink.cpp");
                ReferenceFile("MoviePlayer", "..\\MoviePlayer\\MoviePlayer_bink.hpp");

                ReferenceFile("MusicStateMgr", "..\\MusicStateMgr\\MusicStateMgr.cpp");
                ReferenceFile("MusicStateMgr", "..\\MusicStateMgr\\MusicStateMgr.hpp");

                ReferenceFile("Navigation", "..\\navigation\\AlarmNode.cpp");
                ReferenceFile("Navigation", "..\\navigation\\AlarmNode.hpp");

                ReferenceFile("Obj_mgr\\CollisionMgr", "..\\CollisionMgr\\GridWalker.cpp");
                ReferenceFile("Obj_mgr\\CollisionMgr", "..\\CollisionMgr\\GridWalker.hpp");

                ReferenceFile("Obj_mgr\\InputMgr", "..\\InputMgr\\Monkey.cpp");
                ReferenceFile("Obj_mgr\\InputMgr", "..\\InputMgr\\Monkey.hpp");

                ReferenceFile("Objects", "..\\Objects\\AlienGlob.cpp");
                ReferenceFile("Objects", "..\\Objects\\AlienGlob.hpp");
                ReferenceFile("Objects", "..\\Objects\\Corpse.cpp");
                ReferenceFile("Objects", "..\\Objects\\Corpse.hpp");
                ReferenceFile("Objects", "..\\Objects\\CorpsePain.cpp");
                ReferenceFile("Objects", "..\\Objects\\CorpsePain.hpp");
                ReferenceFile("Objects", "..\\Objects\\Group.cpp");
                ReferenceFile("Objects", "..\\Objects\\Group.hpp");
                ReferenceFile("Objects", "..\\Objects\\LoreObject.cpp");
                ReferenceFile("Objects", "..\\Objects\\LoreObject.hpp");
                ReferenceFile("Objects", "..\\Objects\\PlayerObject.cpp");
                ReferenceFile("Objects", "..\\Objects\\PlayerCombat.cpp");
                ReferenceFile("Objects", "..\\Objects\\PlayerState.cpp");
                ReferenceFile("Objects", "..\\Objects\\PlayerPhysics.cpp");
                ReferenceFile("Objects", "..\\Objects\\PlayerInput.cpp");
                ReferenceFile("Objects", "..\\Objects\\PlayerOnline.cpp");
                ReferenceFile("Objects", "..\\Objects\\NetProjectile.cpp");
                ReferenceFile("Objects", "..\\Objects\\NetProjectile.hpp");
                ReferenceFile("Objects", "..\\Objects\\SuperDestructible.cpp");
                ReferenceFile("Objects", "..\\Objects\\SuperDestructible.hpp");
                ReferenceFile("Objects", "..\\Objects\\ReactiveSurface.cpp");
                ReferenceFile("Objects", "..\\Objects\\ReactiveSurface.hpp");
                ReferenceFile("Objects", "..\\Objects\\AlienSpawnTube.cpp");
                ReferenceFile("Objects", "..\\Objects\\AlienSpawnTube.hpp");
                ReferenceFile("Objects", "..\\Objects\\FlagBase.cpp");
                ReferenceFile("Objects", "..\\Objects\\FlagBase.hpp");
                ReferenceFile("Objects", "..\\Objects\\TeamLight.cpp");
                ReferenceFile("Objects", "..\\Objects\\TeamLight.hpp");
                ReferenceFile("Objects", "..\\Objects\\TeamProp.cpp");
                ReferenceFile("Objects", "..\\Objects\\TeamProp.hpp");
                ReferenceFile("Objects", "..\\Objects\\ForceField.cpp");
                ReferenceFile("Objects", "..\\Objects\\ForceField.hpp");
                ReferenceFile("Objects", "..\\Objects\\Teleporter.cpp");
                ReferenceFile("Objects", "..\\Objects\\Teleporter.hpp");
                ReferenceFile("Objects", "..\\Objects\\JumpPad.cpp");
                ReferenceFile("Objects", "..\\Objects\\JumpPad.hpp");
                ReferenceFile("Objects", "..\\Objects\\Flag.cpp");
                ReferenceFile("Objects", "..\\Objects\\Flag.hpp");
                ReferenceFile("Objects", "..\\Objects\\GameProp.cpp");
                ReferenceFile("Objects", "..\\Objects\\GameProp.hpp");
                ReferenceFile("Objects", "..\\Objects\\Cinema.cpp");
                ReferenceFile("Objects", "..\\Objects\\Cinema.hpp");
                ReferenceFile("Objects", "..\\Objects\\MutagenReservoir.cpp");
                ReferenceFile("Objects", "..\\Objects\\MutagenReservoir.hpp");
                ReferenceFile("Objects", "..\\Objects\\InvisWall.cpp");
                ReferenceFile("Objects", "..\\Objects\\InvisWall.hpp");
                ReferenceFile("Objects", "..\\Objects\\CokeCan.cpp");
                ReferenceFile("Objects", "..\\Objects\\CokeCan.hpp");
                ReferenceFile("Objects", "..\\Objects\\VolumetricLight.cpp");
                ReferenceFile("Objects", "..\\Objects\\VolumetricLight.hpp");
                ReferenceFile("Objects", "..\\Objects\\feedbackemitter.cpp");
                ReferenceFile("Objects", "..\\Objects\\feedbackemitter.hpp");
                ReferenceFile("Objects", "..\\Objects\\GZCoreObj.cpp");
                ReferenceFile("Objects", "..\\Objects\\GZCoreObj.hpp");
                ReferenceFile("Objects", "..\\Objects\\AlienShield.cpp");
                ReferenceFile("Objects", "..\\Objects\\AlienShield.hpp");
                ReferenceFile("Objects", "..\\Objects\\AlienOrb.cpp");
                ReferenceFile("Objects", "..\\Objects\\AlienOrb.hpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMesonSeeker.cpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMesonSeeker.hpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMutantTendril.cpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMutantTendril.hpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMutantContagion.cpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMutantContagion.hpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMutantParasite2.cpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileMutantParasite2.hpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileHoming.cpp");
                ReferenceFile("Objects", "..\\Objects\\ProjectileHoming.hpp");
                ReferenceFile("Objects", "..\\Objects\\LensFilter.cpp");
                ReferenceFile("Objects", "..\\Objects\\LensFilter.hpp");
                ReferenceFile("Objects", "..\\Objects\\Circuit.cpp");
                ReferenceFile("Objects", "..\\Objects\\Circuit.hpp");
                ReferenceFile("Objects", "..\\Objects\\JumpingBeanProjectile.cpp");
                ReferenceFile("Objects", "..\\Objects\\JumpingBeanProjectile.hpp");
                ReferenceFile("Objects", "..\\Objects\\MP_Settings.cpp");
                ReferenceFile("Objects", "..\\Objects\\MP_Settings.hpp");
                ReferenceFile("Objects", "..\\Objects\\BluePrintBag.cpp");
                ReferenceFile("Objects", "..\\Objects\\BluePrintBag.hpp");
                ReferenceFile("Objects", "..\\Objects\\CapPoint.cpp");
                ReferenceFile("Objects", "..\\Objects\\CapPoint.hpp");

                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_alien_grenade_explosion.cpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_alien_grenade_explosion.hpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_cannon.cpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_cannon.hpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_frag_explosion.cpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_frag_explosion.hpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_glass_cluster.cpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_glass_cluster.hpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_meson_lash.cpp");
                ReferenceFile("Objects\\Debris", "..\\Debris\\debris_meson_lash.hpp");

                ReferenceFile("OccluderMgr", "..\\OccluderMgr\\OccluderMgr.cpp");
                ReferenceFile("OccluderMgr", "..\\OccluderMgr\\OccluderMgr.hpp");

                ReferenceFile("PainMgr", "..\\PainMgr\\Pain.cpp");
                ReferenceFile("PainMgr", "..\\PainMgr\\Pain.hpp");
                ReferenceFile("PainMgr", "..\\PainMgr\\PainMgr.cpp");
                ReferenceFile("PainMgr", "..\\PainMgr\\PainMgr.hpp");
                ReferenceFile("PainMgr", "..\\PainMgr\\PainTypes.hpp");

                ExcludeFileReference(filesNode, "Objects", "..\\Objects\\Pain.cpp", configuration);
                ExcludeFileReference(filesNode, "Objects", "..\\Objects\\Pain.hpp", configuration);

                ReferenceFile("PerceptionMgr", "..\\PerceptionMgr\\PerceptionMgr.cpp");
                ReferenceFile("PerceptionMgr", "..\\PerceptionMgr\\PerceptionMgr.hpp");

                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\Collider.cpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\Collider.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\CollisionShape.cpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\CollisionShape.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\Constraint.cpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\Constraint.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\PhysicsInst.cpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\PhysicsInst.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\PhysicsMgr.cpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\PhysicsMgr.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\RigidBody.cpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\RigidBody.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\LinkedList.hpp");
                ReferenceFile("PhysicsMgr", "..\\PhysicsMgr\\Physics.hpp");

                ReferenceFile("Render", "..\\Objects\\Render\\VirtualTextureMask.cpp");
                ReferenceFile("Render", "..\\Objects\\Render\\VirtualTextureMask.hpp");
                ReferenceFile("Render", "..\\Objects\\Render\\VirtualMeshMask.cpp");
                ReferenceFile("Render", "..\\Objects\\Render\\VirtualMeshMask.hpp");

                ReferenceFile("StateMgr", "..\\StateMgr\\GlobalSettings.cpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\GlobalSettings.hpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\LoreList.cpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\LoreList.hpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\MapList.cpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\MapList.hpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\PlayerProfile.cpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\PlayerProfile.hpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\SecretList.cpp");
                ReferenceFile("StateMgr", "..\\StateMgr\\SecretList.hpp");

                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_attack_guid.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_attack_guid.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_base.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_base.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_death.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_death.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_dialog_line.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_dialog_line.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_inventory.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_inventory.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_lookat_guid.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_lookat_guid.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_nav_activation.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_nav_activation.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_pathto_guid.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_pathto_guid.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_play_anim.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_play_anim.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_searchto_guid.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_ai_searchto_guid.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_change_perception.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_change_perception.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_checkpoint.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_checkpoint.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_exit_turret.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_exit_turret.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_fade_geometry.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_fade_geometry.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_man_turret.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_man_turret.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_mission_failed.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_mission_failed.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_player_hud.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_player_hud.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_play_2d_sound.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_play_2d_sound.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_save_game.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_save_game.hpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_screen_fade.cpp");
                ReferenceFile("TriggerEx\\ActionsEx", "..\\TriggerEx\\Actions\\action_screen_fade.hpp");

                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_within_range.cpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_within_range.hpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_line_of_sight.cpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_line_of_sight.hpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_check_focus_object.cpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_check_focus_object.hpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_check_health.cpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_check_health.hpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_is_censored.cpp");
                ReferenceFile("TriggerEx\\ConditionsEx", "..\\TriggerEx\\Conditions\\condition_is_censored.hpp");

                ReferenceFile("TriggerEx\\Meta", "..\\TriggerEx\\Meta\\trigger_meta_cinema_block.cpp");
                ReferenceFile("TriggerEx\\Meta", "..\\TriggerEx\\Meta\\trigger_meta_cinema_block.hpp");

                ReferenceFile("TweakMgr", "..\\TweakMgr\\TweakMgr.cpp");
                ReferenceFile("TweakMgr", "..\\TweakMgr\\TweakMgr.hpp");

                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponScanner.cpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponScanner.hpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponMutation.cpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponMutation.hpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponTRA.cpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponTRA.hpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponBBG.cpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponBBG.hpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponDualShotgun.cpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponDualShotgun.hpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponDualSMP.cpp");
                ReferenceFile("Weapons\\Weapon Objects", "..\\Objects\\WeaponDualSMP.hpp");
            }

            // Print a list of all missing object files.
            string[] missingObjFiles = requiredObjectFiles.Except(filesFound).ToArray();
            if (missingObjFiles.Length > 0)
            {
                for (int i = 0; i < missingObjFiles.Length; i++)
                {
                    Console.WriteLine($"MISSING DEPENDENCY: {missingObjFiles[i]}");
                }
            }
        }

        static void ProcessProjectFile(string originalFile, string outputFile, Dictionary<string, HashSet<string>> LibObjDictionary)
        {
            // Open the project file for reading.
            XmlDocument doc = new XmlDocument();
            doc.Load(originalFile);

            // Remove source control bindings, they're useless now and just cause VS to show annoying dialogs.
            XmlNode projectNode = doc.SelectSingleNode("/VisualStudioProject");
            RemoveAttributeIfFound(projectNode, "SccProjectName");
            RemoveAttributeIfFound(projectNode, "SccAuxPath");
            RemoveAttributeIfFound(projectNode, "SccLocalPath");
            RemoveAttributeIfFound(projectNode, "SccProvider");

            string libFileName = Path.GetFileNameWithoutExtension(outputFile);

            // Check if this is the main project or not and handle accordingly.
            if (libFileName == "A51")
            {
                // Get the configurations node and update xbox configurations.
                XmlNode configurationNode = doc.SelectSingleNode("/VisualStudioProject/Configurations");
                for (int i = configurationNode.ChildNodes.Count - 1; i >= 0; i--)
                {
                    string configurationName = configurationNode.ChildNodes[i].Attributes["Name"].Value;
                    string configurationShort = configurationName.Substring(0, configurationName.IndexOf('|'));

                    // Check the configuration name and handle accordingly.
                    if (configurationName.EndsWith("|Xbox") == true)
                    {
                        // Remove junk configurations.
                        if (configurationName.StartsWith("PS2") == true)
                        {
                            // We should really just delete all PS2 configurations but I'm leaving the non-xbox ones for preservation sake.
                            configurationNode.RemoveChild(configurationNode.ChildNodes[i]);
                            continue;
                        }

                        // Find the compiler and linker tool node.
                        for (int x = 0; x < configurationNode.ChildNodes[i].ChildNodes.Count; x++)
                        {
                            // If this isn't the linker node skip it.
                            XmlNode toolNode = configurationNode.ChildNodes[i].ChildNodes[x];
                            if (toolNode.Attributes["Name"].Value == "VCCLCompilerTool")
                            {
                                // Update attributes for the configuration.
                                //UpdateCompilerConfigurationAttributes(toolNode, configurationShort);
                            }
                            else if (toolNode.Attributes["Name"].Value == "VCLinkerTool")
                            {
                                // Update attributes for the configuration.
                                //UpdateLinkerConfigurationAttributes(toolNode, configurationShort);

                                // Update libraries to link against.
                                toolNode.Attributes["AdditionalDependencies"].Value += " AudioMgr.lib ConversationMgr.lib Gamelib.lib Music_mgr.lib Render.lib StringMgr.lib Entropy.lib Parsing.lib x_files.lib aux_Bitmap.lib EventMgr.lib fx_RunTime.lib NetworkMgr.lib UserInterface.lib binkxbox.lib";

                                //_$(PlatformName)$(ConfigurationName)
                                string[] libDirectories = new string[]
                                {
                                    "$(X)\\..\\Support\\AudioMgr\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\ConversationMgr\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\Gamelib\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\Music_mgr\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\Render\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\StringMgr\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\Entropy\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\Parsing\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\x_files\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\Auxiliary\\Bitmap\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\EventMgr\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\Auxiliary\\fx_RunTime\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\NetworkMgr\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\Support\\Dialogs\\_$(PlatformName)$(ConfigurationName)",
                                    "$(X)\\..\\xCore\\3rdParty\\BinkXbox\\Lib"
                                };

                                // Update library search directories.
                                XmlAttribute libDirAttr = doc.CreateAttribute("AdditionalLibraryDirectories");
                                libDirAttr.Value = string.Join(';', libDirectories);
                                toolNode.Attributes.Append(libDirAttr);
                            }
                        }
                    }
                }

                // Fix build rules for .rdf files to handle file paths with spaces.
                XmlNode filesNode = doc.SelectSingleNode("/VisualStudioProject/Files");
                for (int i = 0; i < filesNode.ChildNodes.Count; i++)
                {
                    if (filesNode.ChildNodes[i].Name == "Filter" && filesNode.ChildNodes[i].Attributes["Name"].Value == "Media")
                    {
                        XmlNode filterNode = filesNode.ChildNodes[i];
                        for (int x = 0; x < filterNode.ChildNodes.Count; x++)
                        {
                            XmlNode fileNode = filterNode.ChildNodes[x];
                            for (int y = 0; y < fileNode.ChildNodes.Count; y++)
                            {
                                XmlNode fileConfigNode = fileNode.ChildNodes[y];
                                string commandLine = fileConfigNode.ChildNodes[0].Attributes["CommandLine"].Value;

                                if (commandLine.EndsWith("saveimage.xbx") == true)
                                    commandLine = "bundler \"$(InputPath)\" -o \"$(SolutionDir)\\media\\saveimage.xbx\"";
                                else if (commandLine.EndsWith("titleimage.xbx") == true)
                                    commandLine = "bundler \"$(InputPath)\" -o \"$(SolutionDir)\\media\\titleimage.xbx\"";

                                fileConfigNode.ChildNodes[0].Attributes["CommandLine"].Value = commandLine;
                            }
                        }
                    }
                }
            }
            else
            {
                // Step 1: Add the xbox platform type.
                XmlNode xboxPlatformNode = doc.CreateElement("Platform");
                xboxPlatformNode.Attributes.Append(doc.CreateAttribute("Name"));
                xboxPlatformNode.Attributes[0].Value = "Xbox";

                XmlNodeList platformNodes = doc.SelectNodes("/VisualStudioProject/Platforms");
                platformNodes[0].AppendChild(xboxPlatformNode);

                // Process configurations for xbox.
                ProcessProjectFileConfiguration(originalFile, outputFile, doc, "Debug", LibObjDictionary);
                ProcessProjectFileConfiguration(originalFile, outputFile, doc, "OptDebug", LibObjDictionary);
                ProcessProjectFileConfiguration(originalFile, outputFile, doc, "QA", LibObjDictionary);
                ProcessProjectFileConfiguration(originalFile, outputFile, doc, "Retail", LibObjDictionary);
            }

            // Save the new project file.
            doc.Save(outputFile);
        }

        static Dictionary<string, HashSet<string>> BuildLibObjListFromMapFile(string mapFile)
        {
            Dictionary<string, HashSet<string>> LibObjDictionary = new Dictionary<string, HashSet<string>>();
            LibObjDictionary.Add("A51", new HashSet<string>());

            // Open the map file for reading.
            using (StreamReader reader = new StreamReader(mapFile))
            {
                // Skip to the symbol list.
                while (reader.ReadLine().StartsWith("  Address         Publics by Value") == false) ;

                // Loop and parse each symbol description.
                while (reader.EndOfStream == false)
                {
                    // Read the current line and make sure it's valid.
                    string line = reader.ReadLine();
                    string[] lineParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (lineParts.Length < 4)
                        continue;

                    string libObj = lineParts[lineParts.Length - 1];
                    if (libObj == "<absolute>")
                        continue;

                    // Parse the lib and object info.
                    string[] libObjParts = libObj.Split(':');
                    if (libObjParts.Length == 1)
                    {
                        // Object file is for the main project.
                        LibObjDictionary["A51"].Add(libObjParts[0]);
                    }
                    else
                    {
                        // Make sure there's an entry for the library.
                        if (LibObjDictionary.ContainsKey(libObjParts[0]) == false)
                            LibObjDictionary.Add(libObjParts[0], new HashSet<string>());

                        LibObjDictionary[libObjParts[0]].Add(libObjParts[1]);
                    }
                }
            }

            return LibObjDictionary;
        }

        static void CleanSlnFile(string filePath)
        {
            // Read all the lines from the clean sln file.
            string[] lines = File.ReadAllLines(filePath);

            StreamWriter writer = new StreamWriter(filePath, false);

            // Remove the source control lines.
            bool prune = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (prune == false && lines[i].Trim().StartsWith("GlobalSection(SourceCodeControl)") == true)
                {
                    prune = true;
                    continue;
                }
                else if (prune == true && lines[i].Trim().StartsWith("EndGlobalSection") == true)
                {
                    prune = false;
                    continue;
                }

                if (prune == false)
                    writer.WriteLine(lines[i]);
            }

            writer.Close();
        }

        static void Main(string[] args)
        {
            System.Text.EncodingProvider provider = System.Text.CodePagesEncodingProvider.Instance;
            Encoding.RegisterProvider(provider);

            if (args.Length != 2)
            {
                Console.WriteLine("VSProjectFixer.exe <clean dir> <updated dir>");
                Console.WriteLine();
                Console.WriteLine("\t<clean dir> \t= unmodified source tree");
                Console.WriteLine("\t<updated dir> \t= source tree to be updated");
                return;
            }

            string sourceDirectory = args[0];
            string destinationDirectory = args[1];

            if (sourceDirectory == destinationDirectory)
            {
                Console.WriteLine("clean dir and updated dir cannot be the same!");
                return;
            }

            string[] projectFilePaths = new string[]
            {
                "Apps\\GameApp\\A51.vcproj",
                "Support\\AudioMgr\\AudioMgr.vcproj",
                "Support\\ConversationMgr\\ConversationMgr.vcproj",
                "Support\\GameLib\\Gamelib.vcproj",
                "Support\\Music_mgr\\Music_mgr.vcproj",
                "Support\\Render\\Render.vcproj",
                "Support\\StringMgr\\StringMgr.vcproj",
                "xCore\\Entropy\\Entropy.vcproj",
                "xCore\\Parsing\\Parsing.vcproj",
                "xCore\\x_files\\x_files.vcproj",
                "xCore\\Auxiliary\\Bitmap\\aux_Bitmap.vcproj",
                "Support\\EventMgr\\EventMgr.vcproj",
                "xCore\\Auxiliary\\fx_RunTime\\fx_RunTime.vcproj",
                "Support\\NetworkMgr\\NetworkMgr.vcproj",
                "Support\\Dialogs\\UserInterface.vcproj"
            };

            // Remove source control bindings from the solution file.
            CleanSlnFile($"{destinationDirectory}\\Apps\\GameApp\\A51.sln");

            // Build a list of object files from the xbox .map file.
            Dictionary<string, HashSet<string>> LibObjDictionary = BuildLibObjListFromMapFile($"{sourceDirectory}\\Apps\\GameApp\\_xboxdebug\\A51.map");

            // Print the list of required libraries.
            Console.WriteLine("Required libraries:");
            for (int i = 0; i < LibObjDictionary.Keys.Count; i++)
            {
                Console.WriteLine($"\t{LibObjDictionary.Keys.ElementAt(i)}");
            }
            Console.WriteLine();

            for (int i = 0; i < projectFilePaths.Length; i++)
            {
                Console.WriteLine($"* Fixing project file '{projectFilePaths[i]}'");
                ProcessProjectFile($"{sourceDirectory}\\{projectFilePaths[i]}", $"{destinationDirectory}\\{projectFilePaths[i]}", LibObjDictionary);

                // If the source control binding file exists rename it so VS doesn't find it.
                string sourceControlBindingFile = $"{destinationDirectory}\\{projectFilePaths[i]}.vspscc";
                if (File.Exists(sourceControlBindingFile) == true)
                {
                    File.Move(sourceControlBindingFile, sourceControlBindingFile + "_");
                }

                Console.WriteLine();
            }
        }
    }
}