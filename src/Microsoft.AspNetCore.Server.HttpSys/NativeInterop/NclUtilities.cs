// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal static class NclUtilities
    {
        internal static bool HasShutdownStarted
        {
            get
            {
                return Environment.HasShutdownStarted
#if !NETSTANDARD1_3
                    || AppDomain.CurrentDomain.IsFinalizingForUnload()
#endif
                    ;
            }
        }
    }
}
