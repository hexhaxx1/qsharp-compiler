﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Generic;
using CommandLine;


namespace Microsoft.Quantum.Sdk.Tools
{
    public class Options
    {

        [Option("QscReferences", Required = false,
        HelpText = "Path to .NET Core assemblies containing rewrite steps to be passed to the Q# compiler.")]
        public IEnumerable<string> QscReferences { get; set; }

        [Option("QscReferencePriorities", Required = false,
        HelpText = "Priority values of the given QscReferences. Needs to have the same length as the given QscReferences.")]
        public IEnumerable<int> QscReferencePriorities { get; set; }

    }
}
