// Copyright 2012 by Advantage Computing Systems, Inc.  All rights reserved.
// No part of this program may be reproduced, in any form or by any means,
// without permission in writing from Advantage Computing Systems, Inc.

using System;

namespace sourcelinkbug;

/// <summary>
/// Indicates that this argument is the default argument.
/// No '/' or '-' prefix only the argument value is specified.
/// The ShortName property should not be set for DefaultArgumentAttribute
/// instances. The LongName property is used for usage text only and
/// does not affect the usage of the argument.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DefaultArgumentAttribute : ArgumentAttribute
{
}
