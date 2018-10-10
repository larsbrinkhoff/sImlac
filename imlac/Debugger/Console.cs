﻿/*  
    This file is part of sImlac.

    sImlac is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    sImlac is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with sImlac.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.IO;

namespace imlac.Debugger
{
    /// <summary>
    /// Defines a node in the debug command tree.
    /// </summary>
    public class DebuggerCommand
    {
        public DebuggerCommand(object instance, string name, String description, String usage, MethodInfo method)
        {
            Instance = instance;
            Name = name.Trim().ToLower();
            Description = description;
            Usage = usage;
            Methods = new List<MethodInfo>(4);

            if (method != null)
            {
                Methods.Add(method);
            }

            SubCommands = new List<DebuggerCommand>();
        }

        public object Instance;
        public string Name;
        public string Description;
        public string Usage;
        public List<MethodInfo> Methods;
        public List<DebuggerCommand> SubCommands;

        public override string ToString()
        {
            if (this.Methods.Count == 0)
            {
                return String.Format("{0}... ({1})", this.Name, this.SubCommands.Count);
            }
            else
            {
                return this.Name;
            }
        }

        public void AddSubNode(List<string> words, MethodInfo method, object instance)
        {
            // We should never hit this case.
            if (words.Count == 0)
            {
                throw new InvalidOperationException("Out of words building command node.");
            }

            // Check the root to see if a node for the first incoming word has already been added
            DebuggerCommand subNode = FindSubNodeByName(words[0]);

            if (subNode == null)
            {
                // No, it has not -- create one and add it now.
                subNode = new DebuggerCommand(instance, words[0], null, null, null);
                this.SubCommands.Add(subNode);

                if (words.Count == 1)
                {
                    // This is the last stop -- set the method and be done with it now.
                    subNode.Methods.Add(method);

                    // early return.
                    return;
                }
            }
            else
            {
                // The node already exists, we will be adding a subnode, hopefully.
                if (words.Count == 1)
                {
                    //
                    // If we're on the last word at this point then this is an overloaded command.
                    // Check that we don't have any other commands with this number of arguments.
                    //
                    int argCount = method.GetParameters().Length;
                    foreach (MethodInfo info in subNode.Methods)
                    {
                        if (info.GetParameters().Length == argCount)
                        {
                            throw new InvalidOperationException("Duplicate overload for console command");
                        }
                    }

                    //
                    // We're ok.  Add it to the method list.
                    //
                    subNode.Methods.Add(method);

                    // and return early.
                    return;
                }
            }

            // We have more words to go.
            words.RemoveAt(0);
            subNode.AddSubNode(words, method, instance);
        }

        public DebuggerCommand FindSubNodeByName(string name)
        {
            DebuggerCommand found = null;

            foreach (DebuggerCommand sub in SubCommands)
            {
                if (sub.Name == name)
                {
                    found = sub;
                    break;
                }
            }

            return found;
        }
    }
    
    public class ConsoleExecutor
    {
        public ConsoleExecutor(params object[] commandObjects)
        {
            List<object> commandList = new List<object>(commandObjects);
            BuildCommandTree(commandList);

            _consolePrompt = new DebuggerPrompt(_commandRoot);
        }
        
        public SystemExecutionState Prompt(ImlacSystem system)
        {
            SystemExecutionState next = SystemExecutionState.Debugging;
            try
            {
                system.PrintProcessorStatus();

                // Get the command string from the prompt.
                string command = _consolePrompt.Prompt().Trim();

                if (string.IsNullOrEmpty(command))
                {
                    command = _lastCommand;
                }

                next = ExecuteLine(command, system);
            
                _lastCommand = command;
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    "Error: {0}",
                    e.InnerException != null ? e.InnerException.Message : e.Message);
            }

            return next;
        }

        public SystemExecutionState ExecuteScript(ImlacSystem system, string scriptFile)
        {
            SystemExecutionState state = SystemExecutionState.Halted;

            using (StreamReader sr = new StreamReader(scriptFile))
            {
                while (!sr.EndOfStream)
                {
                    string line = sr.ReadLine();

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Console.WriteLine(line);
                        state = ExecuteLine(line, system);
                    }
                }
            }

            return state;
        }

        private SystemExecutionState ExecuteLine(string line, ImlacSystem system)
        {
            SystemExecutionState next = SystemExecutionState.Debugging;
                
            if (line.StartsWith("#"))
            {
                // Comments start with "#", just ignore them
            }
            else if(line.StartsWith("@"))
            {
                // A line beginning with an "@" indicates a script to execute.
                string scriptFile = line.Substring(1);

                next = ExecuteScript(system, scriptFile);
            }
            else
            {
                string[] args = null;
                DebuggerCommand command = GetDebuggerCommandFromCommandString(line, out args);

                if (command == null)
                {
                    // Not a command.
                    Console.WriteLine("Invalid command.");
                }
                else
                {
                    next = InvokeConsoleMethod(command, args, system);
                }
            }

            return next;
        }

        private SystemExecutionState InvokeConsoleMethod(DebuggerCommand command, string[] args, ImlacSystem system)
        {   
            MethodInfo method = null;

            //
            // Find the method that matches the arg count we were passed
            // (i.e. handle overloaded commands).
            // That this only matches on argument count is somewhat of a kluge...
            //
            foreach (MethodInfo m in command.Methods)
            {
                ParameterInfo[] paramInfo = m.GetParameters();

                if (args == null && paramInfo.Length == 0 ||
                    paramInfo.Length == args.Length)
                {
                    // found a match
                    method = m;
                    break;
                }
            }

            if (method == null)
            {
                // invalid argument count.                
                throw new ArgumentException(String.Format("Invalid argument count to command."));
            }

            ParameterInfo[] parameterInfo = method.GetParameters(); 
            object[] invokeParams;

            if (args == null)
            {
                invokeParams = null;
            }
            else
            {
                invokeParams = new object[parameterInfo.Length];
            }

            int argIndex = 0;
            for (int paramIndex = 0; paramIndex < parameterInfo.Length; paramIndex++)
            {
                ParameterInfo p = parameterInfo[paramIndex];

                if (p.ParameterType.IsEnum)
                {
                    //
                    // This is an enumeration type.
                    // See if we can find an enumerant that matches the argument.
                    //
                    FieldInfo[] fields = p.ParameterType.GetFields();

                    foreach (FieldInfo f in fields)
                    {
                        if (!f.IsSpecialName && args[argIndex].ToLower() == f.Name.ToLower())
                        {
                            invokeParams[paramIndex] = f.GetRawConstantValue();
                            break;
                        }
                    }

                    if (invokeParams[paramIndex] == null)
                    {
                        // no match, provide possible values
                        StringBuilder sb = new StringBuilder(String.Format("Invalid value for parameter {0}.  Possible values are:", paramIndex));

                        foreach (FieldInfo f in fields)
                        {
                            if (!f.IsSpecialName)
                            {
                                sb.AppendFormat("{0} ", f.Name);
                            }
                        }

                        sb.AppendLine();

                        throw new ArgumentException(sb.ToString());
                    }

                    argIndex++;

                }
                else if (p.ParameterType.IsArray)
                {
                    //
                    // If a function takes an array type, i should do something here, yeah.
                    //
                    argIndex++;
                }
                else
                {
                    if (p.ParameterType == typeof(bool))
                    {
                        invokeParams[paramIndex] = bool.Parse(args[argIndex++]);
                    }
                    else if (p.ParameterType == typeof(uint))
                    {
                        invokeParams[paramIndex] = TryParseUint(args[argIndex++]);
                    }
                    else if (p.ParameterType == typeof(ushort))
                    {
                        invokeParams[paramIndex] = TryParseUshort(args[argIndex++]);
                    }
                    else if (p.ParameterType == typeof(string))
                    {
                        invokeParams[paramIndex] = args[argIndex++];
                    }
                    else if (p.ParameterType == typeof(char))
                    {
                        invokeParams[paramIndex] = (char)args[argIndex++][0];
                    }
                    else if (p.ParameterType == typeof(float))
                    {
                        invokeParams[paramIndex] = float.Parse(args[argIndex++]);
                    }
                    else
                    {
                        throw new ArgumentException(String.Format("Unhandled type for parameter {0}, type {1}", paramIndex, p.ParameterType));
                    }
                }
            }

            //
            // If we've made it THIS far, then we were able to parse all the commands into what they should be.
            // Invoke the method on the object instance associated with the command.
            //
            return (SystemExecutionState)method.Invoke(command.Instance, invokeParams);        
        }
       
        private object GetInstanceFromMethod(MethodInfo method)
        {
            Type instanceType = method.DeclaringType;
            PropertyInfo property = instanceType.GetProperty("Instance");
            return property.GetValue(null, null);
        }

        enum ParseState
        {
            NonWhiteSpace = 0,
            WhiteSpace = 1,
            QuotedString = 2,
        }

        private List<string> SplitArgs(string commandString)
        {
            // We split on whitespace and specially handle quoted strings (quoted strings count as a single arg)
            //
            List<string> args = new List<string>();

            commandString = commandString.Trim();

            StringBuilder sb = new StringBuilder();

            ParseState state = ParseState.NonWhiteSpace;

            foreach(char c in commandString)
            {
                switch (state)
                {
                    case ParseState.NonWhiteSpace:
                        if (char.IsWhiteSpace(c))
                        {
                            // End of token
                            args.Add(sb.ToString());
                            sb.Clear();
                            state = ParseState.WhiteSpace;
                        }
                        else if (c == '\"')
                        {
                            // Start of quoted string
                            state = ParseState.QuotedString;
                        }
                        else
                        {
                            // Character in token
                            sb.Append(c);
                        }
                        break;

                    case ParseState.WhiteSpace:
                        if (!char.IsWhiteSpace(c))
                        {
                            // Start of new token
                            if (c != '\"')
                            {
                                sb.Append(c);
                                state = ParseState.NonWhiteSpace;
                            }
                            else
                            {
                                // Start of quoted string
                                state = ParseState.QuotedString;
                            }
                        }
                        break;

                    case ParseState.QuotedString:
                        if (c == '\"')
                        {
                            // End of quoted string.
                            args.Add(sb.ToString());
                            sb.Clear();
                            state = ParseState.WhiteSpace;
                        }
                        else
                        {
                            // Character in quoted string
                            sb.Append(c);
                        }
                        break;
                }
            }

            if (sb.Length > 0)
            {
                // Add the last token to the args list
                args.Add(sb.ToString());
            }

            return args;
        }

        private DebuggerCommand GetDebuggerCommandFromCommandString(string command, out string[] args)
        {
            args = null;

            List<string> cmdArgs = SplitArgs(command);

            DebuggerCommand current = _commandRoot;
            int commandIndex = 0;

            while (true)
            {
                // If this node has an executor and no subnodes, or if this node has an executor
                // and there are no further arguments, then we're done.
                if ((current.Methods.Count > 0 && current.SubCommands.Count == 0) ||
                    (current.Methods.Count > 0 && commandIndex > cmdArgs.Count -1))
                {
                    break;
                }

                if (commandIndex > cmdArgs.Count - 1)
                {
                    // Out of args with no match.
                    return null;
                }

                // Otherwise we continue down the tree.
                DebuggerCommand next = current.FindSubNodeByName(cmdArgs[commandIndex]);
                
                commandIndex++;

                if (next == null)
                {
                    //
                    // If a matching subcommand was not found, then if we had a previous node with an
                    // executor, use that; otherwise the command is invalid.
                    //
                    if (current.Methods.Count > 0)
                    {
                        break;
                    }
                    else
                    {
                        return null;
                    }
                }

                current = next;
            }

            // Now current should point to the command with the executor
            // and commandIndex should point to the first argument to the command.

            cmdArgs.RemoveRange(0, commandIndex);

            args = cmdArgs.ToArray();
            return current;
        }


        private enum Radix
        {
            Binary = 2,
            Octal = 8,
            Decimal = 10,
            Hexadecimal = 16,
        }

        private static uint TryParseUint(string arg)
        {
            uint result = 0;
            Radix radix = Radix.Decimal;

            switch (arg[0])
            {
                case 'b':
                    radix = Radix.Binary;
                    arg = arg.Remove(0, 1);
                    break;

                case 'o':
                    radix = Radix.Octal;
                    arg = arg.Remove(0, 1);
                    break;

                case 'd':
                    radix = Radix.Decimal;
                    arg = arg.Remove(0, 1);
                    break;

                case 'x':
                    radix = Radix.Hexadecimal;
                    arg = arg.Remove(0, 1);
                    break;

                default:
                    radix = Radix.Octal;
                    break;
            }

            try
            {
                result = Convert.ToUInt32(arg, (int)radix);
            }
            catch
            {
                Console.WriteLine("{0} is not a valid 32-bit value.", arg);
                throw;
            }

            return result;
        }

        private static ushort TryParseUshort(string arg)
        {
            ushort result = 0;
            Radix radix = Radix.Decimal;

            switch (arg[0])
            {
                case 'b':
                    radix = Radix.Binary;
                    arg = arg.Remove(0, 1);
                    break;

                case 'o':
                    radix = Radix.Octal;
                    arg = arg.Remove(0, 1);
                    break;

                case 'd':
                    radix = Radix.Decimal;
                    arg = arg.Remove(0, 1);
                    break;

                case 'x':
                    radix = Radix.Hexadecimal;
                    arg = arg.Remove(0, 1);
                    break;

                default:
                    radix = Radix.Octal;
                    break;
            }

            try
            {
                result = Convert.ToUInt16(arg, (int)radix);
            }
            catch
            {
                Console.WriteLine("{0} is not a valid 16-bit value.", arg);
                throw;
            }

            return result;
        }       

        /// <summary>
        /// Builds the debugger command tree.
        /// </summary>
        private void BuildCommandTree(List<object> commandObjects)
        {
            // Build the flat list which will be built into the tree, by walking
            // the classes that provide the methods
            _commandList = new List<DebuggerCommand>();

            // Add ourself to the list
            commandObjects.Add(this);

            foreach (object commandObject in commandObjects)
            {
                Type type = commandObject.GetType();
                foreach (MethodInfo info in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object[] attribs = info.GetCustomAttributes(typeof(DebuggerFunction), true);

                    if (attribs.Length > 1)
                    {
                        throw new InvalidOperationException(String.Format("More than one ConsoleFunction attribute set on {0}", info.Name));
                    }
                    else if (attribs.Length == 1)
                    {
                        // we have a debugger attribute set on this method
                        // this cast should always succeed given that we're filtering for this type above.
                        DebuggerFunction function = (DebuggerFunction)attribs[0];

                        DebuggerCommand newCommand = new DebuggerCommand(commandObject, function.CommandName, function.Description, function.Usage, info);

                        _commandList.Add(newCommand);
                    }
                }
            }

            // Now actually build the command tree from the above list!
            _commandRoot = new DebuggerCommand(null, "Root", null, null, null);

            foreach (DebuggerCommand c in _commandList)
            {
                string[] commandWords = c.Name.Split(' ');

                // This is kind of ugly, we know that at this point every command built above have only
                // one method.  When building the tree, overloaded commands may end up with more than one.
                _commandRoot.AddSubNode(new List<string>(commandWords), c.Methods[0], c.Instance);
            }
        }

        [DebuggerFunction("show commands", "Shows debugger commands and their descriptions.")]
        private SystemExecutionState ShowCommands()
        {
            foreach (DebuggerCommand cmd in _commandList)
            {
                if (!string.IsNullOrEmpty(cmd.Usage))
                {
                    Console.WriteLine("{0} {2} - {1}", cmd.Name, cmd.Description, cmd.Usage);
                }
                else
                {
                    Console.WriteLine("{0} - {1}", cmd.Name, cmd.Description);
                }
            }

            return SystemExecutionState.Debugging;
        }

        [DebuggerFunction("quit", "Terminates the emulator process")]
        private SystemExecutionState Quit()
        {
            return SystemExecutionState.Quit;
        }

        private DebuggerPrompt _consolePrompt;
        private DebuggerCommand _commandRoot;
        private List<DebuggerCommand> _commandList;

        private string _lastCommand;
    }
}
