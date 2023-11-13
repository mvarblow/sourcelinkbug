// Copyright 2012 by Advantage Computing Systems, Inc.  All rights reserved.
// No part of this program may be reproduced, in any form or by any means,
// without permission in writing from Advantage Computing Systems, Inc.

using System;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;

namespace sourcelinkbug;

internal class Argument
{
    public Argument(ArgumentAttribute? attribute, PropertyInfo property, ErrorReporter errorReporter)
    {
        LongName = ArgumentParser.LongName(attribute, property);
        ExplicitShortName = ArgumentParser.ExplicitShortName(attribute);
        ShortName = ArgumentParser.ShortName(attribute, property);
        CompatibilityName = ArgumentParser.CompatibilityName(attribute);
        HasHelpText = ArgumentParser.HasHelpText(attribute);
        HelpText = ArgumentParser.HelpText(attribute);
        DefaultValue = ArgumentParser.DefaultValue(attribute);
        elementType = ArgumentParser.ElementType(property);
        flags = ArgumentParser.Flags(attribute, property);
        this.property = property;
        SeenValue = false;
        this.errorReporter = errorReporter;
        IsDefault = attribute is DefaultArgumentAttribute;

        if (IsCollection)
        {
            collectionValues = new ArrayList();
        }

        Debug.Assert(!string.IsNullOrEmpty(LongName));
        Debug.Assert(!IsDefault || !ExplicitShortName);
        Debug.Assert(!IsCollection || AllowMultiple, "Collection arguments must have allow multiple");
        Debug.Assert(!Unique || IsCollection, "Unique only applicable to collection arguments");
        Debug.Assert(ArgumentParser.IsValidElementType(Type) ||
            ArgumentParser.IsCollectionType(Type));
        Debug.Assert((IsCollection && ArgumentParser.IsValidElementType(elementType)) ||
            (!IsCollection && elementType == null));
        Debug.Assert(!(IsRequired && HasDefaultValue), "Required arguments cannot have default value");
        Debug.Assert(DefaultValue == null || (DefaultValue.GetType() == Type), "Type of default value must match field type");
    }

    public bool Finish(object destination)
    {
        if (!SeenValue && HasDefaultValue)
        {
            property.SetValue(destination, DefaultValue);
        }
        if (IsCollection)
        {
            property.SetValue(destination, collectionValues!.ToArray(elementType!));
        }

        return ReportMissingRequiredArgument();
    }

    private bool ReportMissingRequiredArgument()
    {
        if (IsRequired && !SeenValue)
        {
            errorReporter.Invoke(IsDefault
                ? string.Format(Xl.MissingRequiredArgument0, LongName)
                : string.Format(Xl.MissingRequiredArgument0Alt1, LongName));
            return true;
        }
        return false;
    }

    private void ReportDuplicateArgumentValue(string? value)
    {
        errorReporter.Invoke(string.Format(Xl.Duplicate0Argument1, LongName, value));
    }

    public bool SetValue(string? value, object destination)
    {
        if (SeenValue && !AllowMultiple)
        {
            errorReporter.Invoke(string.Format(Xl.Duplicate0Argument, LongName));
            return false;
        }
        SeenValue = true;

        if (!ParseValue(ValueType, value, out object? newValue))
        {
            return false;
        }
        if (IsCollection)
        {
            if (Unique && collectionValues!.Contains(newValue))
            {
                ReportDuplicateArgumentValue(value);
                return false;
            }
            collectionValues!.Add(newValue);
        }
        else
        {
            property.SetValue(destination, newValue);
        }

        return true;
    }

    private Type ValueType => IsCollection ? elementType! : Type;

    private void ReportBadArgumentValue(string? value)
    {
        errorReporter.Invoke(string.Format(Xl.IsNotAValidValueForThe1CommandLineOption, value, LongName));
    }

    private bool ParseValue(Type type, string? stringData, [NotNullWhen(true)] out object? value)
    {
        // empty string is never valid
        if (!string.IsNullOrEmpty(stringData))
        {
            try
            {
                if (type == typeof(string))
                {
                    value = stringData;
                    return true;
                }
                if (type == typeof(bool))
                {
                    value = bool.Parse(stringData);
                    return true;
                }
                if (type == typeof(int))
                {
                    value = int.Parse(stringData);
                    return true;
                }
                if (type == typeof(uint))
                {
                    value = int.Parse(stringData);
                    return true;
                }

                Debug.Assert(type.IsEnum);
                value = Enum.Parse(type, stringData == "?" ? "help" : stringData, true);
                return true;
            }
            catch
            {
                // catch parse errors
            }
        }

        ReportBadArgumentValue(stringData);
        value = null;
        return false;
    }

    private static void AppendValue(StringBuilder builder, object value)
    {
        if (value is string || value is int || value is uint || value.GetType().IsEnum || value is bool)
        {
            builder.Append(value);
        }
        else
        {
            bool first = true;
            foreach (object o in (Array)value)
            {
                if (!first)
                {
                    builder.Append(", ");
                }
                AppendValue(builder, o);
                first = false;
            }
        }
    }

    public string? LongName { get; }

    public bool ExplicitShortName { get; }

    public string? ShortName { get; private set; }

    private bool HasShortName => ShortName != null;

    public void ClearShortName()
    {
        ShortName = null;
    }

    public string? CompatibilityName { get; }

    private bool HasHelpText { get; }

    private string? HelpText { get; }

    private object? DefaultValue { get; }

    private bool HasDefaultValue => null != DefaultValue;

    public string FullHelpText
    {
        get
        {
            var builder = new StringBuilder();
            if (HasHelpText)
            {
                builder.Append(HelpText);
            }
            if (DefaultValue != null)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" ");
                }
                builder.Append("Default value: '");
                AppendValue(builder, DefaultValue);
                builder.Append('\'');
            }
            if (HasShortName)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" ");
                }
                builder.Append("(short form /");
                builder.Append(ShortName);
                builder.Append(")");
            }
            return builder.ToString();
        }
    }

    public string SyntaxHelp
    {
        get
        {
            var builder = new StringBuilder();

            if (IsDefault)
            {
                builder.Append("<");
                builder.Append(LongName);
                builder.Append(">");
            }
            else
            {
                Type valueType = ValueType;
                builder.Append("/");
                if (valueType == typeof(bool))
                {
                    builder.Append("[no]");
                }
                builder.Append(LongName);
                if (valueType == typeof(int))
                {
                    builder.Append(":<int>");
                }
                else if (valueType == typeof(uint))
                {
                    builder.Append(":<uint>");
                }
                else if (valueType == typeof(string))
                {
                    builder.Append(":<string>");
                }
                else if (valueType != typeof(bool))
                {
                    Debug.Assert(valueType.IsEnum);

                    builder.Append(":{");
                    bool first = true;
                    foreach (FieldInfo fieldInfo in valueType.GetFields().Where(f => f.IsStatic))
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            builder.Append('|');
                        }
                        builder.Append(fieldInfo.Name);
                    }
                    builder.Append('}');
                }
            }

            return builder.ToString();
        }
    }

    private bool IsRequired => 0 != (flags & ArgumentType.Required);

    private bool SeenValue { get; set; }

    private bool AllowMultiple => 0 != (flags & ArgumentType.Multiple);

    private bool Unique => 0 != (flags & ArgumentType.Unique);

    public Type Type => property.PropertyType.IsGenericType &&
        property.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>)
            ? Nullable.GetUnderlyingType(property.PropertyType)!
            : property.PropertyType;

    private bool IsCollection => ArgumentParser.IsCollectionType(Type);

    private bool IsDefault { get; }

    private readonly PropertyInfo property;
    private readonly Type? elementType;
    private readonly ArgumentType flags;
    private readonly ArrayList? collectionValues;
    private readonly ErrorReporter errorReporter;
}
