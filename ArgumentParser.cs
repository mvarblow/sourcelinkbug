// Copyright 2012 by Advantage Computing Systems, Inc.  All rights reserved.
// No part of this program may be reproduced, in any form or by any means,
// without permission in writing from Advantage Computing Systems, Inc.

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using SuppressMessage = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;

namespace sourcelinkbug;

/// <summary>
/// A delegate used in error reporting.
/// </summary>
public delegate void ErrorReporter(string message);

/// <summary>
/// Parser for command line arguments.
/// The parser specification is inferred from the instance fields of the object
/// specified as the destination of the parse.
/// Valid argument types are: int, uint, string, bool, enums
/// Also argument types of Array of the above types are also valid.
/// Error checking options can be controlled by adding a ArgumentAttribute
/// to the instance fields of the destination object.
/// At most one field may be marked with the DefaultArgumentAttribute
/// indicating that arguments without a '-' or '/' prefix will be parsed as that argument.
/// If not specified then the parser will infer default options for parsing each
/// instance field. The default long name of the argument is the field name. The
/// default short name is the first character of the long name. Long names and explicitly
/// specified short names must be unique. Default short names will be used provided that
/// the default short name does not conflict with a long name or an explicitly
/// specified short name.
/// Arguments which are array types are collection arguments. Collection
/// arguments can be specified multiple times.
/// </summary>
public sealed class ArgumentParser
{
    #region Public static methods to parse arguments

    /// <summary>
    /// Parses Command Line Arguments.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="arguments"> The actual arguments. </param>
    /// <param name="destination"> The resulting parsed arguments. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no errors were detected. </returns>
    public static bool ParseArguments(
        string[] arguments,
        object destination,
        List<string>? unrecognizedArgs = null,
        ErrorReporter? reporter = null)
    {
        reporter ??= Console.WriteLine;
        var parser = new ArgumentParser(destination.GetType(), reporter);
        return parser.Parse(arguments, destination, unrecognizedArgs);
    }

    #endregion

    #region Public methods for help/usage

    /// <summary>
    /// Checks if a set of arguments asks for help.
    /// </summary>
    /// <param name="args"> Arguments to check for help. </param>
    /// <returns> Returns true if arguments contains /? or /help. </returns>
    public static bool ParseHelp(string[] args)
    {
        var helpParser = new ArgumentParser(typeof(HelpArgument), NullErrorReporter);
        var helpArgument = new HelpArgument();
        helpParser.Parse(args, helpArgument);
        return helpArgument.Help;
    }

    /// <summary>
    /// Returns a Usage string for command line argument parsing.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="argumentType"> The type of the arguments to display usage for. </param>
    /// <param name="columns"> The number of columns to format the output to. </param>
    /// <returns> Printable string containing a user friendly description of command line arguments. </returns>
    public static string ArgumentsUsage(Type argumentType, int columns = 80) =>
        new ArgumentParser(argumentType, NullErrorReporter).GetUsageString(columns);

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new command line argument parser.
    /// </summary>
    /// <param name="argumentSpecification"> The type of object to  parse. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    private ArgumentParser(Type argumentSpecification, ErrorReporter reporter)
    {
        this.reporter = reporter;
        arguments = new ArrayList();
        argumentMap = new Hashtable();

        foreach (PropertyInfo property in argumentSpecification.GetProperties().Where(p => p.CanWrite && p.CanRead))
        {
            ArgumentAttribute? attribute = GetAttribute(property);
            if (attribute is DefaultArgumentAttribute)
            {
                Debug.Assert(defaultArgument == null);
                defaultArgument = new Argument(attribute, property, reporter);
                Debug.Assert(defaultArgument.Type != typeof(bool));
            }
            else
            {
                arguments.Add(new Argument(attribute, property, reporter));
            }
        }

        // add explicit names to map
        foreach (Argument argument in arguments)
        {
            Debug.Assert(argument.LongName != null && !argumentMap.ContainsKey(argument.LongName.ToLowerInvariant()));
            if (argument.Type == typeof(bool))
            {
                // booleans argument names must not begin with no (a no prefix on a boolean means "false")
                Debug.Assert(argument.LongName == null ||
                    !argument.LongName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.ShortName == null ||
                    !argument.ShortName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.CompatibilityName == null ||
                    !argument.CompatibilityName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
            }

            // Add the long name to the map
            argumentMap[argument.LongName!.ToLowerInvariant()] = argument;
            if (argument.Type == typeof(bool))
            {
                Debug.Assert(!argumentMap.ContainsKey("no" + argument.LongName.ToLowerInvariant()));
                argumentMap["no" + argument.LongName.ToLowerInvariant()] = argument;
            }

            // Add the short name to the map, if one was explicitly provided
            if (argument.ExplicitShortName)
            {
                if (!string.IsNullOrEmpty(argument.ShortName))
                {
                    Debug.Assert(!argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()));
                    argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                    if (argument.Type == typeof(bool))
                    {
                        // booleans can have NO parameters
                        Debug.Assert(!argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant()));
                        argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                    }
                }
                else
                {
                    argument.ClearShortName();
                }
            }

            // Add the compatibility name to the map, if one was provided
            if (argument.CompatibilityName != null)
            {
                string lowerName = argument.CompatibilityName.ToLowerInvariant();
                Debug.Assert(!argumentMap.ContainsKey(lowerName) || argumentMap[lowerName] == argument);
                argumentMap[lowerName] = argument;
                if (argument.Type == typeof(bool))
                {
                    Debug.Assert(!argumentMap.ContainsKey("no" + lowerName));
                    argumentMap["no" + lowerName] = argument;
                }
            }
        }

        // add implicit names which don't collide to map
        foreach (Argument argument in arguments.Cast<Argument>().Where(argument => !argument.ExplicitShortName))
        {
            if (!string.IsNullOrEmpty(argument.ShortName) && !argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()) &&
                (argument.Type != typeof(bool) || !argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant())))
            {
                argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                if (argument.Type == typeof(bool))
                {
                    argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                }
            }
            else
            {
                argument.ClearShortName();
            }
        }
    }

    #endregion

    #region Public instance methods

    /// <summary>
    /// Parses an argument list.
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no parse errors were encountered</returns>
    private bool Parse(string[] args, object destination, List<string>? unrecognizedArgs = null)
    {
        bool hadError = ParseArgumentList(args, destination, unrecognizedArgs);

        // check for missing required arguments
        hadError = arguments.Cast<Argument>().Aggregate(hadError, (current, arg) => current | arg.Finish(destination));
        if (defaultArgument != null)
        {
            hadError |= defaultArgument.Finish(destination);
        }

        return !hadError;
    }

    /// <summary>
    /// Does this parser have a default argument.
    /// </summary>
    /// <value> Does this parser have a default argument. </value>
    private bool HasDefaultArgument => defaultArgument != null;

    /// <summary>
    /// A user friendly usage string describing the command line argument syntax.
    /// </summary>
    private string GetUsageString(int screenWidth)
    {
        IEnumerable<ArgumentHelpStrings> strings = GetAllHelpStrings().ToList();

        int maxParamLen = strings.Select(helpString => helpString.Syntax.Length).Concat(new[] { 0 }).Max();

        const int minimumNumberOfCharsForHelpText = 10;
        const int minimumHelpTextColumn = 5;
        const int minimumScreenWidth = minimumHelpTextColumn + minimumNumberOfCharsForHelpText;

        int idealMinimumHelpTextColumn = maxParamLen + UsageReportSpacesBeforeParam;
        screenWidth = Math.Max(screenWidth, minimumScreenWidth);
        int helpTextColumn = screenWidth < idealMinimumHelpTextColumn + minimumNumberOfCharsForHelpText
            ? minimumHelpTextColumn
            : idealMinimumHelpTextColumn;

        var builder = new StringBuilder();
        foreach (ArgumentHelpStrings helpStrings in strings)
        {
            // add syntax string
            int syntaxLength = helpStrings.Syntax.Length;
            builder.Append(helpStrings.Syntax);

            // start help text on new line if syntax string is too long
            int currentColumn = syntaxLength;
            if (syntaxLength >= helpTextColumn)
            {
                builder.AppendLine();
                currentColumn = 0;
            }

            // add help text broken on spaces
            int charsPerLine = screenWidth - helpTextColumn;
            int index = 0;
            while (index < helpStrings.Help.Length)
            {
                // tab to start column
                builder.Append(' ', helpTextColumn - currentColumn);

                // find number of chars to display on this line
                int endIndex = index + charsPerLine;
                if (endIndex >= helpStrings.Help.Length)
                {
                    // rest of text fits on this line
                    endIndex = helpStrings.Help.Length;
                }
                else
                {
                    endIndex = helpStrings.Help.LastIndexOf(' ', endIndex - 1, Math.Min(endIndex - index, charsPerLine));
                    if (endIndex <= index)
                    {
                        // no spaces on this line, append full set of chars
                        endIndex = index + charsPerLine;
                    }
                }

                // add chars
                builder.Append(helpStrings.Help, index, endIndex - index);
                index = endIndex;

                // do new line
                builder.AppendLine();
                currentColumn = 0;

                // don't start a new line with spaces
                while (index < helpStrings.Help.Length && helpStrings.Help[index] == ' ')
                {
                    index++;
                }
            }

            // add newline if there's no help text
            if (helpStrings.Help.Length == 0)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    #endregion

    #region Private and internal static methods

    private static ArgumentAttribute? GetAttribute(PropertyInfo property)
    {
        object[] attributes = property.GetCustomAttributes(typeof(ArgumentAttribute), false);
        if (attributes.Length == 1)
        {
            return (ArgumentAttribute)attributes[0];
        }

        Debug.Assert(attributes.Length == 0);
        return null;
    }

    private static ArgumentHelpStrings GetHelpStrings(Argument arg) => new ArgumentHelpStrings(arg.SyntaxHelp, arg.FullHelpText);

    internal static string? LongName(ArgumentAttribute? attribute, PropertyInfo property) =>
        attribute == null || attribute.DefaultLongName ? property.Name : attribute.LongName;

    internal static string? ShortName(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute is DefaultArgumentAttribute)
        {
            return null;
        }
        return ExplicitShortName(attribute) ? attribute?.ShortName : LongName(attribute, property)?[..1];
    }

    internal static string? HelpText(ArgumentAttribute? attribute) => attribute?.HelpText;

    internal static bool HasHelpText(ArgumentAttribute? attribute) => attribute != null && attribute.HasHelpText;

    internal static bool ExplicitShortName(ArgumentAttribute? attribute) => attribute != null && !attribute.DefaultShortName;

    internal static string? CompatibilityName(ArgumentAttribute? attribute) =>
        attribute is DefaultArgumentAttribute ? null : attribute?.CompatibilityName;

    internal static object? DefaultValue(ArgumentAttribute? attribute) =>
        attribute == null || !attribute.HasDefaultValue ? null : attribute.DefaultValue;

    internal static Type? ElementType(PropertyInfo property) =>
        IsCollectionType(property.PropertyType) ? property.PropertyType.GetElementType() : null;

    internal static ArgumentType Flags(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute != null && attribute.Type != 0)
        {
            return attribute.Type;
        }
        return IsCollectionType(property.PropertyType) ? ArgumentType.MultipleUnique : ArgumentType.AtMostOnce;
    }

    internal static bool IsCollectionType(Type type) => type.IsArray;

    internal static bool IsValidElementType(Type? type) =>
        type != null && (
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type.IsEnum);

    #endregion

    #region Private methods and properties

    private void ReportUnrecognizedArgument(string argument)
    {
        reporter.Invoke(string.Format(Xl.UnrecognizedCommandLineArgument0, argument));
    }

    /// <summary>
    /// Parses an argument list into an object
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns> true if an error occurred </returns>
    private bool ParseArgumentList(string[]? args, object destination, List<string>? unrecognizedArgs = null)
    {
        if (args == null)
        {
            return false;
        }

        bool hadError = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!string.IsNullOrEmpty(argument))
            {
                switch (argument[0])
                {
                case '-':
                case '/':
                    int endIndex = argument.IndexOfAny(new[] { ':', '=' }, 1);
                    string optionName = argument.Substring(1, endIndex == -1 ? argument.Length - 1 : endIndex - 1);
                    string? optionArgument = optionName.Length + 1 == argument.Length
                        ? null
                        : argument[(optionName.Length + 2)..];

                    var arg = (Argument?)argumentMap[optionName.ToLowerInvariant()];
                    if (arg == null)
                    {
                        if (unrecognizedArgs == null)
                        {
                            ReportUnrecognizedArgument(argument);
                            hadError = true;
                        }
                        else
                        {
                            unrecognizedArgs.Add(argument);
                        }
                    }
                    else
                    {
                        if (arg.Type == typeof(bool))
                        {
                            Debug.Assert(optionArgument == null); // Booleans should not have option values
                            bool value = arg.LongName != null && string.Equals(optionName,
                                    arg.LongName,
                                    StringComparison.InvariantCultureIgnoreCase) ||
                                arg.ShortName != null && string.Equals(optionName,
                                    arg.ShortName,
                                    StringComparison.InvariantCultureIgnoreCase) ||
                                arg.CompatibilityName != null && string.Equals(optionName,
                                    arg.CompatibilityName,
                                    StringComparison.InvariantCultureIgnoreCase);
                            Debug.Assert(value || optionName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                            optionArgument = value.ToString();
                        }
                        else if (endIndex == -1 && index < args.Length - 1)
                        {
                            // Looks like we're in backward compatibility mode; the value is actually in the next argument element
                            optionArgument = args[++index];
                        }
                        hadError |= !arg.SetValue(optionArgument, destination);
                    }
                    break;
                case '@':
                    hadError |= LexFileArguments(argument[1..], out string[]? nestedArguments);
                    hadError |= ParseArgumentList(nestedArguments, destination, unrecognizedArgs);
                    break;
                default:
                    if (defaultArgument != null)
                    {
                        hadError |= !defaultArgument.SetValue(argument, destination);
                    }
                    else
                    {
                        if (unrecognizedArgs == null)
                        {
                            ReportUnrecognizedArgument(argument);
                            hadError = true;
                        }
                        else
                        {
                            unrecognizedArgs.Add(argument);
                        }
                    }
                    break;
                }
            }
        }

        return hadError;
    }

    private IEnumerable<ArgumentHelpStrings> GetAllHelpStrings()
    {
        var strings = new ArgumentHelpStrings[NumberOfParametersToDisplay];

        int index = 0;
        foreach (Argument arg in arguments)
        {
            strings[index] = GetHelpStrings(arg);
            index++;
        }
        strings[index++] = new ArgumentHelpStrings("@<file>", "Read response file for more options");
        if (defaultArgument != null)
        {
            strings[index] = GetHelpStrings(defaultArgument);
        }

        return strings;
    }

    private int NumberOfParametersToDisplay
    {
        get
        {
            int numberOfParameters = arguments.Count + 1;
            if (HasDefaultArgument)
            {
                numberOfParameters++;
            }
            return numberOfParameters;
        }
    }

    private bool LexFileArguments(string fileName, out string[]? argumentsOutput)
    {
        string args;

        try
        {
            using var file = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            using var streamReader = new StreamReader(file);
            args = streamReader.ReadToEnd();
        }
        catch (Exception e)
        {
            reporter.Invoke(string.Format(Xl.ErrorCantOpenCommandLineArgumentFile01, fileName, e.Message));
            argumentsOutput = null;
            return false;
        }

        bool hadError = false;
        var argArray = new ArrayList();
        var currentArg = new StringBuilder();
        bool inQuotes = false;
        int index = 0;

        // while (index < args.Length)
        try
        {
            while (true)
            {
                // skip whitespace
                while (char.IsWhiteSpace(args[index]))
                {
                    index += 1;
                }

                // # - comment to end of line
                if (args[index] == '#')
                {
                    index += 1;
                    while (args[index] != '\n')
                    {
                        index += 1;
                    }
                    continue;
                }

                // do one argument
                do
                {
                    switch (args[index])
                    {
                        case '\\':
                        {
                            int cSlashes = 1;
                            index += 1;
                            while (index == args.Length && args[index] == '\\')
                            {
                                cSlashes += 1;
                            }

                            if (index == args.Length || args[index] != '"')
                            {
                                currentArg.Append('\\', cSlashes);
                            }
                            else
                            {
                                currentArg.Append('\\', cSlashes >> 1);
                                if (0 != (cSlashes & 1))
                                {
                                    currentArg.Append('"');
                                }
                                else
                                {
                                    inQuotes = !inQuotes;
                                }
                            }
                            break;
                        }
                        case '"':
                            inQuotes = !inQuotes;
                            index += 1;
                            break;
                        default:
                            currentArg.Append(args[index]);
                            index += 1;
                            break;
                    }
                }
                while (!char.IsWhiteSpace(args[index]) || inQuotes);
                argArray.Add(currentArg.ToString());
                currentArg.Length = 0;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // got EOF
            if (inQuotes)
            {
                reporter.Invoke(string.Format(Xl.ErrorUnbalancedInCommandLineArgumentFile0, fileName));
                hadError = true;
            }
            else if (currentArg.Length > 0)
            {
                // valid argument can be terminated by EOF
                argArray.Add(currentArg.ToString());
            }
        }

        argumentsOutput = (string[])argArray.ToArray(typeof(string));
        return hadError;
    }

    #endregion

    #region Private helpers for ParseHelp

    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Local")]
    private class HelpArgument
    {
        [Argument(ShortName = "?")]
        public bool Help { get; set; } = false;
    }

    private static void NullErrorReporter(string message)
    {
    }

    #endregion

    #region Private ArgumentHelpStrings struct

    private readonly struct ArgumentHelpStrings
    {
        public ArgumentHelpStrings(string syntax, string help)
        {
            Syntax = syntax;
            Help = help;
        }

        public readonly string Syntax;
        public readonly string Help;
    }

    #endregion

    #region Private member variables

    private const int UsageReportSpacesBeforeParam = 2;
    private readonly ArrayList arguments;
    private readonly Hashtable argumentMap;
    private readonly Argument? defaultArgument;
    private readonly ErrorReporter reporter;

    #endregion
}

public sealed class ArgumentParser2
{
    #region Public static methods to parse arguments

    /// <summary>
    /// Parses Command Line Arguments.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="arguments"> The actual arguments. </param>
    /// <param name="destination"> The resulting parsed arguments. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no errors were detected. </returns>
    public static bool ParseArguments(
        string[] arguments,
        object destination,
        List<string>? unrecognizedArgs = null,
        ErrorReporter? reporter = null)
    {
        reporter ??= Console.WriteLine;
        var parser = new ArgumentParser2(destination.GetType(), reporter);
        return parser.Parse(arguments, destination, unrecognizedArgs);
    }

    #endregion

    #region Public methods for help/usage

    /// <summary>
    /// Checks if a set of arguments asks for help.
    /// </summary>
    /// <param name="args"> Arguments to check for help. </param>
    /// <returns> Returns true if arguments contains /? or /help. </returns>
    public static bool ParseHelp(string[] args)
    {
        var helpParser = new ArgumentParser2(typeof(HelpArgument), NullErrorReporter);
        var helpArgument = new HelpArgument();
        helpParser.Parse(args, helpArgument);
        return helpArgument.Help;
    }

    /// <summary>
    /// Returns a Usage string for command line argument parsing.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="argumentType"> The type of the arguments to display usage for. </param>
    /// <param name="columns"> The number of columns to format the output to. </param>
    /// <returns> Printable string containing a user friendly description of command line arguments. </returns>
    public static string ArgumentsUsage(Type argumentType, int columns = 80) =>
        new ArgumentParser2(argumentType, NullErrorReporter).GetUsageString(columns);

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new command line argument parser.
    /// </summary>
    /// <param name="argumentSpecification"> The type of object to  parse. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    private ArgumentParser2(Type argumentSpecification, ErrorReporter reporter)
    {
        this.reporter = reporter;
        arguments = new ArrayList();
        argumentMap = new Hashtable();

        foreach (PropertyInfo property in argumentSpecification.GetProperties().Where(p => p.CanWrite && p.CanRead))
        {
            ArgumentAttribute? attribute = GetAttribute(property);
            if (attribute is DefaultArgumentAttribute)
            {
                Debug.Assert(defaultArgument == null);
                defaultArgument = new Argument(attribute, property, reporter);
                Debug.Assert(defaultArgument.Type != typeof(bool));
            }
            else
            {
                arguments.Add(new Argument(attribute, property, reporter));
            }
        }

        // add explicit names to map
        foreach (Argument argument in arguments)
        {
            Debug.Assert(argument.LongName != null && !argumentMap.ContainsKey(argument.LongName.ToLowerInvariant()));
            if (argument.Type == typeof(bool))
            {
                // booleans argument names must not begin with no (a no prefix on a boolean means "false")
                Debug.Assert(argument.LongName == null ||
                    !argument.LongName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.ShortName == null ||
                    !argument.ShortName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.CompatibilityName == null ||
                    !argument.CompatibilityName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
            }

            // Add the long name to the map
            argumentMap[argument.LongName!.ToLowerInvariant()] = argument;
            if (argument.Type == typeof(bool))
            {
                Debug.Assert(!argumentMap.ContainsKey("no" + argument.LongName.ToLowerInvariant()));
                argumentMap["no" + argument.LongName.ToLowerInvariant()] = argument;
            }

            // Add the short name to the map, if one was explicitly provided
            if (argument.ExplicitShortName)
            {
                if (!string.IsNullOrEmpty(argument.ShortName))
                {
                    Debug.Assert(!argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()));
                    argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                    if (argument.Type == typeof(bool))
                    {
                        // booleans can have NO parameters
                        Debug.Assert(!argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant()));
                        argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                    }
                }
                else
                {
                    argument.ClearShortName();
                }
            }

            // Add the compatibility name to the map, if one was provided
            if (argument.CompatibilityName != null)
            {
                string lowerName = argument.CompatibilityName.ToLowerInvariant();
                Debug.Assert(!argumentMap.ContainsKey(lowerName) || argumentMap[lowerName] == argument);
                argumentMap[lowerName] = argument;
                if (argument.Type == typeof(bool))
                {
                    Debug.Assert(!argumentMap.ContainsKey("no" + lowerName));
                    argumentMap["no" + lowerName] = argument;
                }
            }
        }

        // add implicit names which don't collide to map
        foreach (Argument argument in arguments.Cast<Argument>().Where(argument => !argument.ExplicitShortName))
        {
            if (!string.IsNullOrEmpty(argument.ShortName) && !argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()) &&
                (argument.Type != typeof(bool) || !argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant())))
            {
                argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                if (argument.Type == typeof(bool))
                {
                    argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                }
            }
            else
            {
                argument.ClearShortName();
            }
        }
    }

    #endregion

    #region Public instance methods

    /// <summary>
    /// Parses an argument list.
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no parse errors were encountered</returns>
    private bool Parse(string[] args, object destination, List<string>? unrecognizedArgs = null)
    {
        bool hadError = ParseArgumentList(args, destination, unrecognizedArgs);

        // check for missing required arguments
        hadError = arguments.Cast<Argument>().Aggregate(hadError, (current, arg) => current | arg.Finish(destination));
        if (defaultArgument != null)
        {
            hadError |= defaultArgument.Finish(destination);
        }

        return !hadError;
    }

    /// <summary>
    /// Does this parser have a default argument.
    /// </summary>
    /// <value> Does this parser have a default argument. </value>
    private bool HasDefaultArgument => defaultArgument != null;

    /// <summary>
    /// A user friendly usage string describing the command line argument syntax.
    /// </summary>
    private string GetUsageString(int screenWidth)
    {
        IEnumerable<ArgumentHelpStrings> strings = GetAllHelpStrings().ToList();

        int maxParamLen = strings.Select(helpString => helpString.Syntax.Length).Concat(new[] { 0 }).Max();

        const int minimumNumberOfCharsForHelpText = 10;
        const int minimumHelpTextColumn = 5;
        const int minimumScreenWidth = minimumHelpTextColumn + minimumNumberOfCharsForHelpText;

        int idealMinimumHelpTextColumn = maxParamLen + UsageReportSpacesBeforeParam;
        screenWidth = Math.Max(screenWidth, minimumScreenWidth);
        int helpTextColumn = screenWidth < idealMinimumHelpTextColumn + minimumNumberOfCharsForHelpText
            ? minimumHelpTextColumn
            : idealMinimumHelpTextColumn;

        var builder = new StringBuilder();
        foreach (ArgumentHelpStrings helpStrings in strings)
        {
            // add syntax string
            int syntaxLength = helpStrings.Syntax.Length;
            builder.Append(helpStrings.Syntax);

            // start help text on new line if syntax string is too long
            int currentColumn = syntaxLength;
            if (syntaxLength >= helpTextColumn)
            {
                builder.AppendLine();
                currentColumn = 0;
            }

            // add help text broken on spaces
            int charsPerLine = screenWidth - helpTextColumn;
            int index = 0;
            while (index < helpStrings.Help.Length)
            {
                // tab to start column
                builder.Append(' ', helpTextColumn - currentColumn);

                // find number of chars to display on this line
                int endIndex = index + charsPerLine;
                if (endIndex >= helpStrings.Help.Length)
                {
                    // rest of text fits on this line
                    endIndex = helpStrings.Help.Length;
                }
                else
                {
                    endIndex = helpStrings.Help.LastIndexOf(' ', endIndex - 1, Math.Min(endIndex - index, charsPerLine));
                    if (endIndex <= index)
                    {
                        // no spaces on this line, append full set of chars
                        endIndex = index + charsPerLine;
                    }
                }

                // add chars
                builder.Append(helpStrings.Help, index, endIndex - index);
                index = endIndex;

                // do new line
                builder.AppendLine();
                currentColumn = 0;

                // don't start a new line with spaces
                while (index < helpStrings.Help.Length && helpStrings.Help[index] == ' ')
                {
                    index++;
                }
            }

            // add newline if there's no help text
            if (helpStrings.Help.Length == 0)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    #endregion

    #region Private and internal static methods

    private static ArgumentAttribute? GetAttribute(PropertyInfo property)
    {
        object[] attributes = property.GetCustomAttributes(typeof(ArgumentAttribute), false);
        if (attributes.Length == 1)
        {
            return (ArgumentAttribute)attributes[0];
        }

        Debug.Assert(attributes.Length == 0);
        return null;
    }

    private static ArgumentHelpStrings GetHelpStrings(Argument arg) => new ArgumentHelpStrings(arg.SyntaxHelp, arg.FullHelpText);

    internal static string? LongName(ArgumentAttribute? attribute, PropertyInfo property) =>
        attribute == null || attribute.DefaultLongName ? property.Name : attribute.LongName;

    internal static string? ShortName(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute is DefaultArgumentAttribute)
        {
            return null;
        }
        return ExplicitShortName(attribute) ? attribute?.ShortName : LongName(attribute, property)?[..1];
    }

    internal static string? HelpText(ArgumentAttribute? attribute) => attribute?.HelpText;

    internal static bool HasHelpText(ArgumentAttribute? attribute) => attribute != null && attribute.HasHelpText;

    internal static bool ExplicitShortName(ArgumentAttribute? attribute) => attribute != null && !attribute.DefaultShortName;

    internal static string? CompatibilityName(ArgumentAttribute? attribute) =>
        attribute is DefaultArgumentAttribute ? null : attribute?.CompatibilityName;

    internal static object? DefaultValue(ArgumentAttribute? attribute) =>
        attribute == null || !attribute.HasDefaultValue ? null : attribute.DefaultValue;

    internal static Type? ElementType(PropertyInfo property) =>
        IsCollectionType(property.PropertyType) ? property.PropertyType.GetElementType() : null;

    internal static ArgumentType Flags(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute != null && attribute.Type != 0)
        {
            return attribute.Type;
        }
        return IsCollectionType(property.PropertyType) ? ArgumentType.MultipleUnique : ArgumentType.AtMostOnce;
    }

    internal static bool IsCollectionType(Type type) => type.IsArray;

    internal static bool IsValidElementType(Type? type) =>
        type != null && (
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type.IsEnum);

    #endregion

    #region Private methods and properties

    private void ReportUnrecognizedArgument(string argument)
    {
        reporter.Invoke(string.Format(Xl.UnrecognizedCommandLineArgument0, argument));
    }

    /// <summary>
    /// Parses an argument list into an object
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns> true if an error occurred </returns>
    private bool ParseArgumentList(string[]? args, object destination, List<string>? unrecognizedArgs = null)
    {
        if (args == null)
        {
            return false;
        }

        bool hadError = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!string.IsNullOrEmpty(argument))
            {
                switch (argument[0])
                {
                    case '-':
                    case '/':
                        int endIndex = argument.IndexOfAny(new[] { ':', '=' }, 1);
                        string optionName = argument.Substring(1, endIndex == -1 ? argument.Length - 1 : endIndex - 1);
                        string? optionArgument = optionName.Length + 1 == argument.Length
                            ? null
                            : argument[(optionName.Length + 2)..];

                        var arg = (Argument?)argumentMap[optionName.ToLowerInvariant()];
                        if (arg == null)
                        {
                            if (unrecognizedArgs == null)
                            {
                                ReportUnrecognizedArgument(argument);
                                hadError = true;
                            }
                            else
                            {
                                unrecognizedArgs.Add(argument);
                            }
                        }
                        else
                        {
                            if (arg.Type == typeof(bool))
                            {
                                Debug.Assert(optionArgument == null); // Booleans should not have option values
                                bool value = arg.LongName != null && string.Equals(optionName,
                                        arg.LongName,
                                        StringComparison.InvariantCultureIgnoreCase) ||
                                    arg.ShortName != null && string.Equals(optionName,
                                        arg.ShortName,
                                        StringComparison.InvariantCultureIgnoreCase) ||
                                    arg.CompatibilityName != null && string.Equals(optionName,
                                        arg.CompatibilityName,
                                        StringComparison.InvariantCultureIgnoreCase);
                                Debug.Assert(value || optionName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                                optionArgument = value.ToString();
                            }
                            else if (endIndex == -1 && index < args.Length - 1)
                            {
                                // Looks like we're in backward compatibility mode; the value is actually in the next argument element
                                optionArgument = args[++index];
                            }
                            hadError |= !arg.SetValue(optionArgument, destination);
                        }
                        break;
                    case '@':
                        hadError |= LexFileArguments(argument[1..], out string[]? nestedArguments);
                        hadError |= ParseArgumentList(nestedArguments, destination, unrecognizedArgs);
                        break;
                    default:
                        if (defaultArgument != null)
                        {
                            hadError |= !defaultArgument.SetValue(argument, destination);
                        }
                        else
                        {
                            if (unrecognizedArgs == null)
                            {
                                ReportUnrecognizedArgument(argument);
                                hadError = true;
                            }
                            else
                            {
                                unrecognizedArgs.Add(argument);
                            }
                        }
                        break;
                }
            }
        }

        return hadError;
    }

    private IEnumerable<ArgumentHelpStrings> GetAllHelpStrings()
    {
        var strings = new ArgumentHelpStrings[NumberOfParametersToDisplay];

        int index = 0;
        foreach (Argument arg in arguments)
        {
            strings[index] = GetHelpStrings(arg);
            index++;
        }
        strings[index++] = new ArgumentHelpStrings("@<file>", "Read response file for more options");
        if (defaultArgument != null)
        {
            strings[index] = GetHelpStrings(defaultArgument);
        }

        return strings;
    }

    private int NumberOfParametersToDisplay
    {
        get
        {
            int numberOfParameters = arguments.Count + 1;
            if (HasDefaultArgument)
            {
                numberOfParameters++;
            }
            return numberOfParameters;
        }
    }

    private bool LexFileArguments(string fileName, out string[]? argumentsOutput)
    {
        string args;

        try
        {
            using var file = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            using var streamReader = new StreamReader(file);
            args = streamReader.ReadToEnd();
        }
        catch (Exception e)
        {
            reporter.Invoke(string.Format(Xl.ErrorCantOpenCommandLineArgumentFile01, fileName, e.Message));
            argumentsOutput = null;
            return false;
        }

        bool hadError = false;
        var argArray = new ArrayList();
        var currentArg = new StringBuilder();
        bool inQuotes = false;
        int index = 0;

        // while (index < args.Length)
        try
        {
            while (true)
            {
                // skip whitespace
                while (char.IsWhiteSpace(args[index]))
                {
                    index += 1;
                }

                // # - comment to end of line
                if (args[index] == '#')
                {
                    index += 1;
                    while (args[index] != '\n')
                    {
                        index += 1;
                    }
                    continue;
                }

                // do one argument
                do
                {
                    switch (args[index])
                    {
                        case '\\':
                            {
                                int cSlashes = 1;
                                index += 1;
                                while (index == args.Length && args[index] == '\\')
                                {
                                    cSlashes += 1;
                                }

                                if (index == args.Length || args[index] != '"')
                                {
                                    currentArg.Append('\\', cSlashes);
                                }
                                else
                                {
                                    currentArg.Append('\\', cSlashes >> 1);
                                    if (0 != (cSlashes & 1))
                                    {
                                        currentArg.Append('"');
                                    }
                                    else
                                    {
                                        inQuotes = !inQuotes;
                                    }
                                }
                                break;
                            }
                        case '"':
                            inQuotes = !inQuotes;
                            index += 1;
                            break;
                        default:
                            currentArg.Append(args[index]);
                            index += 1;
                            break;
                    }
                }
                while (!char.IsWhiteSpace(args[index]) || inQuotes);
                argArray.Add(currentArg.ToString());
                currentArg.Length = 0;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // got EOF
            if (inQuotes)
            {
                reporter.Invoke(string.Format(Xl.ErrorUnbalancedInCommandLineArgumentFile0, fileName));
                hadError = true;
            }
            else if (currentArg.Length > 0)
            {
                // valid argument can be terminated by EOF
                argArray.Add(currentArg.ToString());
            }
        }

        argumentsOutput = (string[])argArray.ToArray(typeof(string));
        return hadError;
    }

    #endregion

    #region Private helpers for ParseHelp

    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Local")]
    private class HelpArgument
    {
        [Argument(ShortName = "?")]
        public bool Help { get; set; } = false;
    }

    private static void NullErrorReporter(string message)
    {
    }

    #endregion

    #region Private ArgumentHelpStrings struct

    private readonly struct ArgumentHelpStrings
    {
        public ArgumentHelpStrings(string syntax, string help)
        {
            Syntax = syntax;
            Help = help;
        }

        public readonly string Syntax;
        public readonly string Help;
    }

    #endregion

    #region Private member variables

    private const int UsageReportSpacesBeforeParam = 2;
    private readonly ArrayList arguments;
    private readonly Hashtable argumentMap;
    private readonly Argument? defaultArgument;
    private readonly ErrorReporter reporter;

    #endregion
}

public sealed class ArgumentParser3
{
    #region Public static methods to parse arguments

    /// <summary>
    /// Parses Command Line Arguments.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="arguments"> The actual arguments. </param>
    /// <param name="destination"> The resulting parsed arguments. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no errors were detected. </returns>
    public static bool ParseArguments(
        string[] arguments,
        object destination,
        List<string>? unrecognizedArgs = null,
        ErrorReporter? reporter = null)
    {
        reporter ??= Console.WriteLine;
        var parser = new ArgumentParser3(destination.GetType(), reporter);
        return parser.Parse(arguments, destination, unrecognizedArgs);
    }

    #endregion

    #region Public methods for help/usage

    /// <summary>
    /// Checks if a set of arguments asks for help.
    /// </summary>
    /// <param name="args"> Arguments to check for help. </param>
    /// <returns> Returns true if arguments contains /? or /help. </returns>
    public static bool ParseHelp(string[] args)
    {
        var helpParser = new ArgumentParser3(typeof(HelpArgument), NullErrorReporter);
        var helpArgument = new HelpArgument();
        helpParser.Parse(args, helpArgument);
        return helpArgument.Help;
    }

    /// <summary>
    /// Returns a Usage string for command line argument parsing.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="argumentType"> The type of the arguments to display usage for. </param>
    /// <param name="columns"> The number of columns to format the output to. </param>
    /// <returns> Printable string containing a user friendly description of command line arguments. </returns>
    public static string ArgumentsUsage(Type argumentType, int columns = 80) =>
        new ArgumentParser3(argumentType, NullErrorReporter).GetUsageString(columns);

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new command line argument parser.
    /// </summary>
    /// <param name="argumentSpecification"> The type of object to  parse. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    private ArgumentParser3(Type argumentSpecification, ErrorReporter reporter)
    {
        this.reporter = reporter;
        arguments = new ArrayList();
        argumentMap = new Hashtable();

        foreach (PropertyInfo property in argumentSpecification.GetProperties().Where(p => p.CanWrite && p.CanRead))
        {
            ArgumentAttribute? attribute = GetAttribute(property);
            if (attribute is DefaultArgumentAttribute)
            {
                Debug.Assert(defaultArgument == null);
                defaultArgument = new Argument(attribute, property, reporter);
                Debug.Assert(defaultArgument.Type != typeof(bool));
            }
            else
            {
                arguments.Add(new Argument(attribute, property, reporter));
            }
        }

        // add explicit names to map
        foreach (Argument argument in arguments)
        {
            Debug.Assert(argument.LongName != null && !argumentMap.ContainsKey(argument.LongName.ToLowerInvariant()));
            if (argument.Type == typeof(bool))
            {
                // booleans argument names must not begin with no (a no prefix on a boolean means "false")
                Debug.Assert(argument.LongName == null ||
                    !argument.LongName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.ShortName == null ||
                    !argument.ShortName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.CompatibilityName == null ||
                    !argument.CompatibilityName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
            }

            // Add the long name to the map
            argumentMap[argument.LongName!.ToLowerInvariant()] = argument;
            if (argument.Type == typeof(bool))
            {
                Debug.Assert(!argumentMap.ContainsKey("no" + argument.LongName.ToLowerInvariant()));
                argumentMap["no" + argument.LongName.ToLowerInvariant()] = argument;
            }

            // Add the short name to the map, if one was explicitly provided
            if (argument.ExplicitShortName)
            {
                if (!string.IsNullOrEmpty(argument.ShortName))
                {
                    Debug.Assert(!argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()));
                    argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                    if (argument.Type == typeof(bool))
                    {
                        // booleans can have NO parameters
                        Debug.Assert(!argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant()));
                        argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                    }
                }
                else
                {
                    argument.ClearShortName();
                }
            }

            // Add the compatibility name to the map, if one was provided
            if (argument.CompatibilityName != null)
            {
                string lowerName = argument.CompatibilityName.ToLowerInvariant();
                Debug.Assert(!argumentMap.ContainsKey(lowerName) || argumentMap[lowerName] == argument);
                argumentMap[lowerName] = argument;
                if (argument.Type == typeof(bool))
                {
                    Debug.Assert(!argumentMap.ContainsKey("no" + lowerName));
                    argumentMap["no" + lowerName] = argument;
                }
            }
        }

        // add implicit names which don't collide to map
        foreach (Argument argument in arguments.Cast<Argument>().Where(argument => !argument.ExplicitShortName))
        {
            if (!string.IsNullOrEmpty(argument.ShortName) && !argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()) &&
                (argument.Type != typeof(bool) || !argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant())))
            {
                argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                if (argument.Type == typeof(bool))
                {
                    argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                }
            }
            else
            {
                argument.ClearShortName();
            }
        }
    }

    #endregion

    #region Public instance methods

    /// <summary>
    /// Parses an argument list.
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no parse errors were encountered</returns>
    private bool Parse(string[] args, object destination, List<string>? unrecognizedArgs = null)
    {
        bool hadError = ParseArgumentList(args, destination, unrecognizedArgs);

        // check for missing required arguments
        hadError = arguments.Cast<Argument>().Aggregate(hadError, (current, arg) => current | arg.Finish(destination));
        if (defaultArgument != null)
        {
            hadError |= defaultArgument.Finish(destination);
        }

        return !hadError;
    }

    /// <summary>
    /// Does this parser have a default argument.
    /// </summary>
    /// <value> Does this parser have a default argument. </value>
    private bool HasDefaultArgument => defaultArgument != null;

    /// <summary>
    /// A user friendly usage string describing the command line argument syntax.
    /// </summary>
    private string GetUsageString(int screenWidth)
    {
        IEnumerable<ArgumentHelpStrings> strings = GetAllHelpStrings().ToList();

        int maxParamLen = strings.Select(helpString => helpString.Syntax.Length).Concat(new[] { 0 }).Max();

        const int minimumNumberOfCharsForHelpText = 10;
        const int minimumHelpTextColumn = 5;
        const int minimumScreenWidth = minimumHelpTextColumn + minimumNumberOfCharsForHelpText;

        int idealMinimumHelpTextColumn = maxParamLen + UsageReportSpacesBeforeParam;
        screenWidth = Math.Max(screenWidth, minimumScreenWidth);
        int helpTextColumn = screenWidth < idealMinimumHelpTextColumn + minimumNumberOfCharsForHelpText
            ? minimumHelpTextColumn
            : idealMinimumHelpTextColumn;

        var builder = new StringBuilder();
        foreach (ArgumentHelpStrings helpStrings in strings)
        {
            // add syntax string
            int syntaxLength = helpStrings.Syntax.Length;
            builder.Append(helpStrings.Syntax);

            // start help text on new line if syntax string is too long
            int currentColumn = syntaxLength;
            if (syntaxLength >= helpTextColumn)
            {
                builder.AppendLine();
                currentColumn = 0;
            }

            // add help text broken on spaces
            int charsPerLine = screenWidth - helpTextColumn;
            int index = 0;
            while (index < helpStrings.Help.Length)
            {
                // tab to start column
                builder.Append(' ', helpTextColumn - currentColumn);

                // find number of chars to display on this line
                int endIndex = index + charsPerLine;
                if (endIndex >= helpStrings.Help.Length)
                {
                    // rest of text fits on this line
                    endIndex = helpStrings.Help.Length;
                }
                else
                {
                    endIndex = helpStrings.Help.LastIndexOf(' ', endIndex - 1, Math.Min(endIndex - index, charsPerLine));
                    if (endIndex <= index)
                    {
                        // no spaces on this line, append full set of chars
                        endIndex = index + charsPerLine;
                    }
                }

                // add chars
                builder.Append(helpStrings.Help, index, endIndex - index);
                index = endIndex;

                // do new line
                builder.AppendLine();
                currentColumn = 0;

                // don't start a new line with spaces
                while (index < helpStrings.Help.Length && helpStrings.Help[index] == ' ')
                {
                    index++;
                }
            }

            // add newline if there's no help text
            if (helpStrings.Help.Length == 0)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    #endregion

    #region Private and internal static methods

    private static ArgumentAttribute? GetAttribute(PropertyInfo property)
    {
        object[] attributes = property.GetCustomAttributes(typeof(ArgumentAttribute), false);
        if (attributes.Length == 1)
        {
            return (ArgumentAttribute)attributes[0];
        }

        Debug.Assert(attributes.Length == 0);
        return null;
    }

    private static ArgumentHelpStrings GetHelpStrings(Argument arg) => new ArgumentHelpStrings(arg.SyntaxHelp, arg.FullHelpText);

    internal static string? LongName(ArgumentAttribute? attribute, PropertyInfo property) =>
        attribute == null || attribute.DefaultLongName ? property.Name : attribute.LongName;

    internal static string? ShortName(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute is DefaultArgumentAttribute)
        {
            return null;
        }
        return ExplicitShortName(attribute) ? attribute?.ShortName : LongName(attribute, property)?[..1];
    }

    internal static string? HelpText(ArgumentAttribute? attribute) => attribute?.HelpText;

    internal static bool HasHelpText(ArgumentAttribute? attribute) => attribute != null && attribute.HasHelpText;

    internal static bool ExplicitShortName(ArgumentAttribute? attribute) => attribute != null && !attribute.DefaultShortName;

    internal static string? CompatibilityName(ArgumentAttribute? attribute) =>
        attribute is DefaultArgumentAttribute ? null : attribute?.CompatibilityName;

    internal static object? DefaultValue(ArgumentAttribute? attribute) =>
        attribute == null || !attribute.HasDefaultValue ? null : attribute.DefaultValue;

    internal static Type? ElementType(PropertyInfo property) =>
        IsCollectionType(property.PropertyType) ? property.PropertyType.GetElementType() : null;

    internal static ArgumentType Flags(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute != null && attribute.Type != 0)
        {
            return attribute.Type;
        }
        return IsCollectionType(property.PropertyType) ? ArgumentType.MultipleUnique : ArgumentType.AtMostOnce;
    }

    internal static bool IsCollectionType(Type type) => type.IsArray;

    internal static bool IsValidElementType(Type? type) =>
        type != null && (
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type.IsEnum);

    #endregion

    #region Private methods and properties

    private void ReportUnrecognizedArgument(string argument)
    {
        reporter.Invoke(string.Format(Xl.UnrecognizedCommandLineArgument0, argument));
    }

    /// <summary>
    /// Parses an argument list into an object
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns> true if an error occurred </returns>
    private bool ParseArgumentList(string[]? args, object destination, List<string>? unrecognizedArgs = null)
    {
        if (args == null)
        {
            return false;
        }

        bool hadError = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!string.IsNullOrEmpty(argument))
            {
                switch (argument[0])
                {
                    case '-':
                    case '/':
                        int endIndex = argument.IndexOfAny(new[] { ':', '=' }, 1);
                        string optionName = argument.Substring(1, endIndex == -1 ? argument.Length - 1 : endIndex - 1);
                        string? optionArgument = optionName.Length + 1 == argument.Length
                            ? null
                            : argument[(optionName.Length + 2)..];

                        var arg = (Argument?)argumentMap[optionName.ToLowerInvariant()];
                        if (arg == null)
                        {
                            if (unrecognizedArgs == null)
                            {
                                ReportUnrecognizedArgument(argument);
                                hadError = true;
                            }
                            else
                            {
                                unrecognizedArgs.Add(argument);
                            }
                        }
                        else
                        {
                            if (arg.Type == typeof(bool))
                            {
                                Debug.Assert(optionArgument == null); // Booleans should not have option values
                                bool value = arg.LongName != null && string.Equals(optionName,
                                        arg.LongName,
                                        StringComparison.InvariantCultureIgnoreCase) ||
                                    arg.ShortName != null && string.Equals(optionName,
                                        arg.ShortName,
                                        StringComparison.InvariantCultureIgnoreCase) ||
                                    arg.CompatibilityName != null && string.Equals(optionName,
                                        arg.CompatibilityName,
                                        StringComparison.InvariantCultureIgnoreCase);
                                Debug.Assert(value || optionName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                                optionArgument = value.ToString();
                            }
                            else if (endIndex == -1 && index < args.Length - 1)
                            {
                                // Looks like we're in backward compatibility mode; the value is actually in the next argument element
                                optionArgument = args[++index];
                            }
                            hadError |= !arg.SetValue(optionArgument, destination);
                        }
                        break;
                    case '@':
                        hadError |= LexFileArguments(argument[1..], out string[]? nestedArguments);
                        hadError |= ParseArgumentList(nestedArguments, destination, unrecognizedArgs);
                        break;
                    default:
                        if (defaultArgument != null)
                        {
                            hadError |= !defaultArgument.SetValue(argument, destination);
                        }
                        else
                        {
                            if (unrecognizedArgs == null)
                            {
                                ReportUnrecognizedArgument(argument);
                                hadError = true;
                            }
                            else
                            {
                                unrecognizedArgs.Add(argument);
                            }
                        }
                        break;
                }
            }
        }

        return hadError;
    }

    private IEnumerable<ArgumentHelpStrings> GetAllHelpStrings()
    {
        var strings = new ArgumentHelpStrings[NumberOfParametersToDisplay];

        int index = 0;
        foreach (Argument arg in arguments)
        {
            strings[index] = GetHelpStrings(arg);
            index++;
        }
        strings[index++] = new ArgumentHelpStrings("@<file>", "Read response file for more options");
        if (defaultArgument != null)
        {
            strings[index] = GetHelpStrings(defaultArgument);
        }

        return strings;
    }

    private int NumberOfParametersToDisplay
    {
        get
        {
            int numberOfParameters = arguments.Count + 1;
            if (HasDefaultArgument)
            {
                numberOfParameters++;
            }
            return numberOfParameters;
        }
    }

    private bool LexFileArguments(string fileName, out string[]? argumentsOutput)
    {
        string args;

        try
        {
            using var file = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            using var streamReader = new StreamReader(file);
            args = streamReader.ReadToEnd();
        }
        catch (Exception e)
        {
            reporter.Invoke(string.Format(Xl.ErrorCantOpenCommandLineArgumentFile01, fileName, e.Message));
            argumentsOutput = null;
            return false;
        }

        bool hadError = false;
        var argArray = new ArrayList();
        var currentArg = new StringBuilder();
        bool inQuotes = false;
        int index = 0;

        // while (index < args.Length)
        try
        {
            while (true)
            {
                // skip whitespace
                while (char.IsWhiteSpace(args[index]))
                {
                    index += 1;
                }

                // # - comment to end of line
                if (args[index] == '#')
                {
                    index += 1;
                    while (args[index] != '\n')
                    {
                        index += 1;
                    }
                    continue;
                }

                // do one argument
                do
                {
                    switch (args[index])
                    {
                        case '\\':
                            {
                                int cSlashes = 1;
                                index += 1;
                                while (index == args.Length && args[index] == '\\')
                                {
                                    cSlashes += 1;
                                }

                                if (index == args.Length || args[index] != '"')
                                {
                                    currentArg.Append('\\', cSlashes);
                                }
                                else
                                {
                                    currentArg.Append('\\', cSlashes >> 1);
                                    if (0 != (cSlashes & 1))
                                    {
                                        currentArg.Append('"');
                                    }
                                    else
                                    {
                                        inQuotes = !inQuotes;
                                    }
                                }
                                break;
                            }
                        case '"':
                            inQuotes = !inQuotes;
                            index += 1;
                            break;
                        default:
                            currentArg.Append(args[index]);
                            index += 1;
                            break;
                    }
                }
                while (!char.IsWhiteSpace(args[index]) || inQuotes);
                argArray.Add(currentArg.ToString());
                currentArg.Length = 0;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // got EOF
            if (inQuotes)
            {
                reporter.Invoke(string.Format(Xl.ErrorUnbalancedInCommandLineArgumentFile0, fileName));
                hadError = true;
            }
            else if (currentArg.Length > 0)
            {
                // valid argument can be terminated by EOF
                argArray.Add(currentArg.ToString());
            }
        }

        argumentsOutput = (string[])argArray.ToArray(typeof(string));
        return hadError;
    }

    #endregion

    #region Private helpers for ParseHelp

    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Local")]
    private class HelpArgument
    {
        [Argument(ShortName = "?")]
        public bool Help { get; set; } = false;
    }

    private static void NullErrorReporter(string message)
    {
    }

    #endregion

    #region Private ArgumentHelpStrings struct

    private readonly struct ArgumentHelpStrings
    {
        public ArgumentHelpStrings(string syntax, string help)
        {
            Syntax = syntax;
            Help = help;
        }

        public readonly string Syntax;
        public readonly string Help;
    }

    #endregion

    #region Private member variables

    private const int UsageReportSpacesBeforeParam = 2;
    private readonly ArrayList arguments;
    private readonly Hashtable argumentMap;
    private readonly Argument? defaultArgument;
    private readonly ErrorReporter reporter;

    #endregion
}

public sealed class ArgumentParser4
{
    #region Public static methods to parse arguments

    /// <summary>
    /// Parses Command Line Arguments.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="arguments"> The actual arguments. </param>
    /// <param name="destination"> The resulting parsed arguments. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no errors were detected. </returns>
    public static bool ParseArguments(
        string[] arguments,
        object destination,
        List<string>? unrecognizedArgs = null,
        ErrorReporter? reporter = null)
    {
        reporter ??= Console.WriteLine;
        var parser = new ArgumentParser4(destination.GetType(), reporter);
        return parser.Parse(arguments, destination, unrecognizedArgs);
    }

    #endregion

    #region Public methods for help/usage

    /// <summary>
    /// Checks if a set of arguments asks for help.
    /// </summary>
    /// <param name="args"> Arguments to check for help. </param>
    /// <returns> Returns true if arguments contains /? or /help. </returns>
    public static bool ParseHelp(string[] args)
    {
        var helpParser = new ArgumentParser4(typeof(HelpArgument), NullErrorReporter);
        var helpArgument = new HelpArgument();
        helpParser.Parse(args, helpArgument);
        return helpArgument.Help;
    }

    /// <summary>
    /// Returns a Usage string for command line argument parsing.
    /// Use ArgumentAttributes to control parsing behavior.
    /// </summary>
    /// <param name="argumentType"> The type of the arguments to display usage for. </param>
    /// <param name="columns"> The number of columns to format the output to. </param>
    /// <returns> Printable string containing a user friendly description of command line arguments. </returns>
    public static string ArgumentsUsage(Type argumentType, int columns = 80) =>
        new ArgumentParser4(argumentType, NullErrorReporter).GetUsageString(columns);

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new command line argument parser.
    /// </summary>
    /// <param name="argumentSpecification"> The type of object to  parse. </param>
    /// <param name="reporter"> The destination for parse errors. </param>
    private ArgumentParser4(Type argumentSpecification, ErrorReporter reporter)
    {
        this.reporter = reporter;
        arguments = new ArrayList();
        argumentMap = new Hashtable();

        foreach (PropertyInfo property in argumentSpecification.GetProperties().Where(p => p.CanWrite && p.CanRead))
        {
            ArgumentAttribute? attribute = GetAttribute(property);
            if (attribute is DefaultArgumentAttribute)
            {
                Debug.Assert(defaultArgument == null);
                defaultArgument = new Argument(attribute, property, reporter);
                Debug.Assert(defaultArgument.Type != typeof(bool));
            }
            else
            {
                arguments.Add(new Argument(attribute, property, reporter));
            }
        }

        // add explicit names to map
        foreach (Argument argument in arguments)
        {
            Debug.Assert(argument.LongName != null && !argumentMap.ContainsKey(argument.LongName.ToLowerInvariant()));
            if (argument.Type == typeof(bool))
            {
                // booleans argument names must not begin with no (a no prefix on a boolean means "false")
                Debug.Assert(argument.LongName == null ||
                    !argument.LongName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.ShortName == null ||
                    !argument.ShortName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                Debug.Assert(argument.CompatibilityName == null ||
                    !argument.CompatibilityName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
            }

            // Add the long name to the map
            argumentMap[argument.LongName!.ToLowerInvariant()] = argument;
            if (argument.Type == typeof(bool))
            {
                Debug.Assert(!argumentMap.ContainsKey("no" + argument.LongName.ToLowerInvariant()));
                argumentMap["no" + argument.LongName.ToLowerInvariant()] = argument;
            }

            // Add the short name to the map, if one was explicitly provided
            if (argument.ExplicitShortName)
            {
                if (!string.IsNullOrEmpty(argument.ShortName))
                {
                    Debug.Assert(!argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()));
                    argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                    if (argument.Type == typeof(bool))
                    {
                        // booleans can have NO parameters
                        Debug.Assert(!argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant()));
                        argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                    }
                }
                else
                {
                    argument.ClearShortName();
                }
            }

            // Add the compatibility name to the map, if one was provided
            if (argument.CompatibilityName != null)
            {
                string lowerName = argument.CompatibilityName.ToLowerInvariant();
                Debug.Assert(!argumentMap.ContainsKey(lowerName) || argumentMap[lowerName] == argument);
                argumentMap[lowerName] = argument;
                if (argument.Type == typeof(bool))
                {
                    Debug.Assert(!argumentMap.ContainsKey("no" + lowerName));
                    argumentMap["no" + lowerName] = argument;
                }
            }
        }

        // add implicit names which don't collide to map
        foreach (Argument argument in arguments.Cast<Argument>().Where(argument => !argument.ExplicitShortName))
        {
            if (!string.IsNullOrEmpty(argument.ShortName) && !argumentMap.ContainsKey(argument.ShortName.ToLowerInvariant()) &&
                (argument.Type != typeof(bool) || !argumentMap.ContainsKey("no" + argument.ShortName.ToLowerInvariant())))
            {
                argumentMap[argument.ShortName.ToLowerInvariant()] = argument;
                if (argument.Type == typeof(bool))
                {
                    argumentMap["no" + argument.ShortName.ToLowerInvariant()] = argument;
                }
            }
            else
            {
                argument.ClearShortName();
            }
        }
    }

    #endregion

    #region Public instance methods

    /// <summary>
    /// Parses an argument list.
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns>True if no parse errors were encountered</returns>
    private bool Parse(string[] args, object destination, List<string>? unrecognizedArgs = null)
    {
        bool hadError = ParseArgumentList(args, destination, unrecognizedArgs);

        // check for missing required arguments
        hadError = arguments.Cast<Argument>().Aggregate(hadError, (current, arg) => current | arg.Finish(destination));
        if (defaultArgument != null)
        {
            hadError |= defaultArgument.Finish(destination);
        }

        return !hadError;
    }

    /// <summary>
    /// Does this parser have a default argument.
    /// </summary>
    /// <value> Does this parser have a default argument. </value>
    private bool HasDefaultArgument => defaultArgument != null;

    /// <summary>
    /// A user friendly usage string describing the command line argument syntax.
    /// </summary>
    private string GetUsageString(int screenWidth)
    {
        IEnumerable<ArgumentHelpStrings> strings = GetAllHelpStrings().ToList();

        int maxParamLen = strings.Select(helpString => helpString.Syntax.Length).Concat(new[] { 0 }).Max();

        const int minimumNumberOfCharsForHelpText = 10;
        const int minimumHelpTextColumn = 5;
        const int minimumScreenWidth = minimumHelpTextColumn + minimumNumberOfCharsForHelpText;

        int idealMinimumHelpTextColumn = maxParamLen + UsageReportSpacesBeforeParam;
        screenWidth = Math.Max(screenWidth, minimumScreenWidth);
        int helpTextColumn = screenWidth < idealMinimumHelpTextColumn + minimumNumberOfCharsForHelpText
            ? minimumHelpTextColumn
            : idealMinimumHelpTextColumn;

        var builder = new StringBuilder();
        foreach (ArgumentHelpStrings helpStrings in strings)
        {
            // add syntax string
            int syntaxLength = helpStrings.Syntax.Length;
            builder.Append(helpStrings.Syntax);

            // start help text on new line if syntax string is too long
            int currentColumn = syntaxLength;
            if (syntaxLength >= helpTextColumn)
            {
                builder.AppendLine();
                currentColumn = 0;
            }

            // add help text broken on spaces
            int charsPerLine = screenWidth - helpTextColumn;
            int index = 0;
            while (index < helpStrings.Help.Length)
            {
                // tab to start column
                builder.Append(' ', helpTextColumn - currentColumn);

                // find number of chars to display on this line
                int endIndex = index + charsPerLine;
                if (endIndex >= helpStrings.Help.Length)
                {
                    // rest of text fits on this line
                    endIndex = helpStrings.Help.Length;
                }
                else
                {
                    endIndex = helpStrings.Help.LastIndexOf(' ', endIndex - 1, Math.Min(endIndex - index, charsPerLine));
                    if (endIndex <= index)
                    {
                        // no spaces on this line, append full set of chars
                        endIndex = index + charsPerLine;
                    }
                }

                // add chars
                builder.Append(helpStrings.Help, index, endIndex - index);
                index = endIndex;

                // do new line
                builder.AppendLine();
                currentColumn = 0;

                // don't start a new line with spaces
                while (index < helpStrings.Help.Length && helpStrings.Help[index] == ' ')
                {
                    index++;
                }
            }

            // add newline if there's no help text
            if (helpStrings.Help.Length == 0)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    #endregion

    #region Private and internal static methods

    private static ArgumentAttribute? GetAttribute(PropertyInfo property)
    {
        object[] attributes = property.GetCustomAttributes(typeof(ArgumentAttribute), false);
        if (attributes.Length == 1)
        {
            return (ArgumentAttribute)attributes[0];
        }

        Debug.Assert(attributes.Length == 0);
        return null;
    }

    private static ArgumentHelpStrings GetHelpStrings(Argument arg) => new ArgumentHelpStrings(arg.SyntaxHelp, arg.FullHelpText);

    internal static string? LongName(ArgumentAttribute? attribute, PropertyInfo property) =>
        attribute == null || attribute.DefaultLongName ? property.Name : attribute.LongName;

    internal static string? ShortName(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute is DefaultArgumentAttribute)
        {
            return null;
        }
        return ExplicitShortName(attribute) ? attribute?.ShortName : LongName(attribute, property)?[..1];
    }

    internal static string? HelpText(ArgumentAttribute? attribute) => attribute?.HelpText;

    internal static bool HasHelpText(ArgumentAttribute? attribute) => attribute != null && attribute.HasHelpText;

    internal static bool ExplicitShortName(ArgumentAttribute? attribute) => attribute != null && !attribute.DefaultShortName;

    internal static string? CompatibilityName(ArgumentAttribute? attribute) =>
        attribute is DefaultArgumentAttribute ? null : attribute?.CompatibilityName;

    internal static object? DefaultValue(ArgumentAttribute? attribute) =>
        attribute == null || !attribute.HasDefaultValue ? null : attribute.DefaultValue;

    internal static Type? ElementType(PropertyInfo property) =>
        IsCollectionType(property.PropertyType) ? property.PropertyType.GetElementType() : null;

    internal static ArgumentType Flags(ArgumentAttribute? attribute, PropertyInfo property)
    {
        if (attribute != null && attribute.Type != 0)
        {
            return attribute.Type;
        }
        return IsCollectionType(property.PropertyType) ? ArgumentType.MultipleUnique : ArgumentType.AtMostOnce;
    }

    internal static bool IsCollectionType(Type type) => type.IsArray;

    internal static bool IsValidElementType(Type? type) =>
        type != null && (
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type.IsEnum);

    #endregion

    #region Private methods and properties

    private void ReportUnrecognizedArgument(string argument)
    {
        reporter.Invoke(string.Format(Xl.UnrecognizedCommandLineArgument0, argument));
    }

    /// <summary>
    /// Parses an argument list into an object
    /// </summary>
    /// <param name="args">The arguments to parse.</param>
    /// <param name="destination">The object to set with the values of the parsed arguments.</param>
    /// <param name="unrecognizedArgs">A list to hold the unrecognized arguments; null to report them as errors.</param>
    /// <returns> true if an error occurred </returns>
    private bool ParseArgumentList(string[]? args, object destination, List<string>? unrecognizedArgs = null)
    {
        if (args == null)
        {
            return false;
        }

        bool hadError = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (!string.IsNullOrEmpty(argument))
            {
                switch (argument[0])
                {
                    case '-':
                    case '/':
                        int endIndex = argument.IndexOfAny(new[] { ':', '=' }, 1);
                        string optionName = argument.Substring(1, endIndex == -1 ? argument.Length - 1 : endIndex - 1);
                        string? optionArgument = optionName.Length + 1 == argument.Length
                            ? null
                            : argument[(optionName.Length + 2)..];

                        var arg = (Argument?)argumentMap[optionName.ToLowerInvariant()];
                        if (arg == null)
                        {
                            if (unrecognizedArgs == null)
                            {
                                ReportUnrecognizedArgument(argument);
                                hadError = true;
                            }
                            else
                            {
                                unrecognizedArgs.Add(argument);
                            }
                        }
                        else
                        {
                            if (arg.Type == typeof(bool))
                            {
                                Debug.Assert(optionArgument == null); // Booleans should not have option values
                                bool value = arg.LongName != null && string.Equals(optionName,
                                        arg.LongName,
                                        StringComparison.InvariantCultureIgnoreCase) ||
                                    arg.ShortName != null && string.Equals(optionName,
                                        arg.ShortName,
                                        StringComparison.InvariantCultureIgnoreCase) ||
                                    arg.CompatibilityName != null && string.Equals(optionName,
                                        arg.CompatibilityName,
                                        StringComparison.InvariantCultureIgnoreCase);
                                Debug.Assert(value || optionName.StartsWith("no", StringComparison.InvariantCultureIgnoreCase));
                                optionArgument = value.ToString();
                            }
                            else if (endIndex == -1 && index < args.Length - 1)
                            {
                                // Looks like we're in backward compatibility mode; the value is actually in the next argument element
                                optionArgument = args[++index];
                            }
                            hadError |= !arg.SetValue(optionArgument, destination);
                        }
                        break;
                    case '@':
                        hadError |= LexFileArguments(argument[1..], out string[]? nestedArguments);
                        hadError |= ParseArgumentList(nestedArguments, destination, unrecognizedArgs);
                        break;
                    default:
                        if (defaultArgument != null)
                        {
                            hadError |= !defaultArgument.SetValue(argument, destination);
                        }
                        else
                        {
                            if (unrecognizedArgs == null)
                            {
                                ReportUnrecognizedArgument(argument);
                                hadError = true;
                            }
                            else
                            {
                                unrecognizedArgs.Add(argument);
                            }
                        }
                        break;
                }
            }
        }

        return hadError;
    }

    private IEnumerable<ArgumentHelpStrings> GetAllHelpStrings()
    {
        var strings = new ArgumentHelpStrings[NumberOfParametersToDisplay];

        int index = 0;
        foreach (Argument arg in arguments)
        {
            strings[index] = GetHelpStrings(arg);
            index++;
        }
        strings[index++] = new ArgumentHelpStrings("@<file>", "Read response file for more options");
        if (defaultArgument != null)
        {
            strings[index] = GetHelpStrings(defaultArgument);
        }

        return strings;
    }

    private int NumberOfParametersToDisplay
    {
        get
        {
            int numberOfParameters = arguments.Count + 1;
            if (HasDefaultArgument)
            {
                numberOfParameters++;
            }
            return numberOfParameters;
        }
    }

    private bool LexFileArguments(string fileName, out string[]? argumentsOutput)
    {
        string args;

        try
        {
            using var file = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            using var streamReader = new StreamReader(file);
            args = streamReader.ReadToEnd();
        }
        catch (Exception e)
        {
            reporter.Invoke(string.Format(Xl.ErrorCantOpenCommandLineArgumentFile01, fileName, e.Message));
            argumentsOutput = null;
            return false;
        }

        bool hadError = false;
        var argArray = new ArrayList();
        var currentArg = new StringBuilder();
        bool inQuotes = false;
        int index = 0;

        // while (index < args.Length)
        try
        {
            while (true)
            {
                // skip whitespace
                while (char.IsWhiteSpace(args[index]))
                {
                    index += 1;
                }

                // # - comment to end of line
                if (args[index] == '#')
                {
                    index += 1;
                    while (args[index] != '\n')
                    {
                        index += 1;
                    }
                    continue;
                }

                // do one argument
                do
                {
                    switch (args[index])
                    {
                        case '\\':
                            {
                                int cSlashes = 1;
                                index += 1;
                                while (index == args.Length && args[index] == '\\')
                                {
                                    cSlashes += 1;
                                }

                                if (index == args.Length || args[index] != '"')
                                {
                                    currentArg.Append('\\', cSlashes);
                                }
                                else
                                {
                                    currentArg.Append('\\', cSlashes >> 1);
                                    if (0 != (cSlashes & 1))
                                    {
                                        currentArg.Append('"');
                                    }
                                    else
                                    {
                                        inQuotes = !inQuotes;
                                    }
                                }
                                break;
                            }
                        case '"':
                            inQuotes = !inQuotes;
                            index += 1;
                            break;
                        default:
                            currentArg.Append(args[index]);
                            index += 1;
                            break;
                    }
                }
                while (!char.IsWhiteSpace(args[index]) || inQuotes);
                argArray.Add(currentArg.ToString());
                currentArg.Length = 0;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // got EOF
            if (inQuotes)
            {
                reporter.Invoke(string.Format(Xl.ErrorUnbalancedInCommandLineArgumentFile0, fileName));
                hadError = true;
            }
            else if (currentArg.Length > 0)
            {
                // valid argument can be terminated by EOF
                argArray.Add(currentArg.ToString());
            }
        }

        argumentsOutput = (string[])argArray.ToArray(typeof(string));
        return hadError;
    }

    #endregion

    #region Private helpers for ParseHelp

    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Local")]
    private class HelpArgument
    {
        [Argument(ShortName = "?")]
        public bool Help { get; set; } = false;
    }

    private static void NullErrorReporter(string message)
    {
    }

    #endregion

    #region Private ArgumentHelpStrings struct

    private readonly struct ArgumentHelpStrings
    {
        public ArgumentHelpStrings(string syntax, string help)
        {
            Syntax = syntax;
            Help = help;
        }

        public readonly string Syntax;
        public readonly string Help;
    }

    #endregion

    #region Private member variables

    private const int UsageReportSpacesBeforeParam = 2;
    private readonly ArrayList arguments;
    private readonly Hashtable argumentMap;
    private readonly Argument? defaultArgument;
    private readonly ErrorReporter reporter;

    #endregion
}
