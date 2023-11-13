// Copyright 2012 by Advantage Computing Systems, Inc.  All rights reserved.
// No part of this program may be reproduced, in any form or by any means,
// without permission in writing from Advantage Computing Systems, Inc.

using System;
using System.Diagnostics;
using SuppressMessage = System.Diagnostics.CodeAnalysis.SuppressMessageAttribute;

namespace sourcelinkbug;

/// <summary>
/// Allows control of command line parsing.
/// Attach this attribute to instance fields of types used
/// as the destination of command line argument parsing.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
[SuppressMessage("Performance", "CA1813:Avoid unsealed attributes", Justification = "Low impact violation uses attribute hierarchy")]
public class ArgumentAttribute : Attribute
{
    /// <summary>
    /// The error checking to be done on the argument.
    /// </summary>
    public ArgumentType Type { get; set; }

    /// <summary>
    /// Returns true if the argument did not have an explicit short name specified.
    /// </summary>
    public bool DefaultShortName => null == shortName;

    /// <summary>
    /// The short name of the argument.
    /// Set to null means use the default short name if it does not
    /// conflict with any other parameter name.
    /// Set to String.Empty for no short name.
    /// This property should not be set for DefaultArgumentAttributes.
    /// </summary>
    public string? ShortName
    {
        get => shortName;
        set
        {
            Debug.Assert(value == null || this is not DefaultArgumentAttribute);
            shortName = value;
        }
    }

    /// <summary>
    /// Returns true if the argument did not have an explicit long name specified.
    /// </summary>
    public bool DefaultLongName => null == longName;

    /// <summary>
    /// The long name of the argument.
    /// Set to null means use the default long name.
    /// The long name for every argument must be unique.
    /// It is an error to specify a long name of String.Empty.
    /// </summary>
    public string? LongName
    {
        get
        {
            Debug.Assert(!DefaultLongName);
            return longName;
        }
        set
        {
            Debug.Assert(value != "");
            longName = value;
        }
    }

    /// <summary>
    /// The default value of the argument.
    /// </summary>
    public object? DefaultValue { get; set; }

    /// <summary>
    /// Returns true if the argument has a default value.
    /// </summary>
    public bool HasDefaultValue => null != DefaultValue;

    /// <summary>
    /// Returns true if the argument has help text specified.
    /// </summary>
    public bool HasHelpText => null != HelpText;

    /// <summary>
    /// The help text for the argument.
    /// </summary>
    public string? HelpText { get; set; }

    /// <summary>
    /// The name of the compatibility-mode version of the argument.  Each argument can have
    /// one compatibility mode name to allow for backward compatibility if an argument is
    /// renamed.
    /// </summary>
    public string? CompatibilityName { get; set; }

    /// <summary>
    /// Returns true if the argument has a compatibility name.
    /// </summary>
    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    public bool HasCompatibilityName => null != CompatibilityName;

    private string? shortName;
    private string? longName;
}
