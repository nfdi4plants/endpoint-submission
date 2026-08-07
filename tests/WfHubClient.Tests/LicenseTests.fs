module WfHubClient.Tests.LicenseTests

open Expecto
open WfHubClient

/// Opening phrases of the real license texts, wrapped the way a LICENSE file wraps them.
let private texts =
    [ "MIT",
      "MIT License\n\nCopyright (c) 2026 Somebody\n\nPermission is hereby granted, free of charge, to any\nperson obtaining a copy of this software and associated\ndocumentation files (the \"Software\"), to deal in the Software\nwithout restriction."
      "Apache-2.0",
      "                                 Apache License\n                           Version 2.0, January 2004\n                        http://www.apache.org/licenses/\n\n   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION"
      "BSD-3-Clause",
      "BSD 3-Clause License\n\nRedistribution and use in source and binary forms, with or without\nmodification, are permitted provided that the following conditions are met:\n\n3. Neither the name of the copyright holder nor the names of its contributors\n   may be used to endorse products derived from this software."
      "BSD-2-Clause",
      "BSD 2-Clause License\n\nRedistribution and use in source and binary forms, with or without\nmodification, are permitted provided that the following conditions are met:\n\n2. Redistributions in binary form must reproduce the above copyright notice."
      "GPL-3.0-only",
      "                    GNU GENERAL PUBLIC LICENSE\n                       Version 3, 29 June 2007\n\n Copyright (C) 2007 Free Software Foundation, Inc."
      "GPL-2.0-only",
      "                    GNU GENERAL PUBLIC LICENSE\n                       Version 2, June 1991\n\n Copyright (C) 1989, 1991 Free Software Foundation, Inc."
      "LGPL-3.0-only",
      "                   GNU LESSER GENERAL PUBLIC LICENSE\n                       Version 3, 29 June 2007\n\n This version of the GNU Lesser General Public License incorporates the terms\n and conditions of version 3 of the GNU General Public License."
      "AGPL-3.0-only",
      "                    GNU AFFERO GENERAL PUBLIC LICENSE\n                       Version 3, 19 November 2007\n\n Copyright (C) 2007 Free Software Foundation, Inc."
      "MPL-2.0",
      "Mozilla Public License Version 2.0\n==================================\n\n1. Definitions\n--------------"
      "CC0-1.0",
      "Creative Commons Legal Code\n\nCC0 1.0 Universal\n\n    CREATIVE COMMONS CORPORATION IS NOT A LAW FIRM."
      "CC-BY-4.0",
      "Creative Commons Attribution 4.0 International Public License\n\nBy exercising the Licensed Rights, You accept and agree to be bound."
      "CC-BY-SA-4.0",
      "Creative Commons Attribution-ShareAlike 4.0 International Public License\n\nBy exercising the Licensed Rights, You accept and agree to be bound."
      "Unlicense",
      "This is free and unencumbered software released into the public domain.\n\nAnyone is free to copy, modify, publish, use, compile, sell, or distribute this\nsoftware." ]

[<Tests>]
let tests =
    testList "licenses" [
        testList "recognized texts" [
            for expected, text in texts ->
                testCase expected <| fun () ->
                    Expect.equal (Licenses.trySpdxIdentifier text) (Some expected) "wrong identifier"
        ]

        testCase "a file holding only an identifier is taken at its word" <| fun () ->
            Expect.equal (Licenses.trySpdxIdentifier "MIT") (Some "MIT") "wrong identifier"
            Expect.equal (Licenses.trySpdxIdentifier "  cc-by-4.0\n") (Some "CC-BY-4.0") "wrong identifier"

        testCase "unrecognized and empty licenses map to nothing" <| fun () ->
            Expect.isNone (Licenses.trySpdxIdentifier "This fixture is published under the MIT license.") "a mention is not the license text"
            Expect.isNone (Licenses.trySpdxIdentifier "Ask the authors before using any of this.") "expected no identifier"
            Expect.isNone (Licenses.trySpdxIdentifier "   \n  ") "expected no identifier for blank text"

        testCase "every identifier resolves to its SPDX url" <| fun () ->
            Expect.equal (Licenses.spdxUrl "MIT") "https://spdx.org/licenses/MIT" "wrong url"

            for identifier in Licenses.identifiers do
                Expect.stringStarts (Licenses.spdxUrl identifier) "https://spdx.org/licenses/" "wrong url shape"
    ]
