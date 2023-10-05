# <img src="PLCSIM.UnitTest/Resources/PLCSIM.UnitTest.png" alt="PLCSIM.CmdRunner Icon" width="48" height="48" align="top"> PLCSIM.UnitTest
[![GitHub license](https://img.shields.io/github/license/Naereen/StrapDown.js.svg)](https://github.com/Lorenz-Software/PLCSIM.UnitTest/blob/master/LICENSE)
[![Open Source? Yes!](https://badgen.net/badge/Open%20Source%20%3F/Yes%21/blue?icon=github)](https://github.com/Lorenz-Software/PLCSIM.UnitTest)

## Description

Visual Studio solution for executing unit tests for TIA Portal projects on PLCSIM Advanced.

Plugin framework to support different versions of TIA Openess and PLCSIM Advanced.

## System Requirements

- Visual Studio 2019
- .NET Framework 4.8
- PLCSIM Advanced v5.0
- TIA Openess
    - v16
    - v17
    - v18
- TIA Portal unit test library for communication with unit test runner

## Projects

- Common library projects
    - ApplicationUtilities: Common functionalities
    - PlcSimAdvanced: Contract for PlcSimAdvanced plugins & Common functionality
    - TiaOpeness: Contract for TIA Openess plugins & Common functionality
- Plugin projects
    - PlcSimAdvanced.V5.0: Plugin for PLCSIM Advanced API v5.0
    - TiaOpeness.V16: Plugin for TIA Openess V16
    - TiaOpeness.V17: Plugin for TIA Openess V17
    - TiaOpeness.V18: Plugin for TIA Openess V18
- PLCSIM.UnitTest: Desktop application for testing unit test communication with PLCSIM Advanced instance
- PLCSIM.UnitTest.CommandLine: Unit test commandline runner (see [README](PLCSIM.UnitTest.CommandLine/README.md))

## Resources

- Directory "./Plugins/" 
    - Binaries from plugin projects.<br/>
      Therefore it is not necessary to have PLCSIM Advanced or TIA Portal installed to compile command line runner application.
- Directory "./TIA Portal Libraries/"
    - Libraries for use in TIA Portal projects

## Authors and acknowledgment

D. Lorenz

## License

MIT

## Project status

Alpha: Not tested in production stage but support and feedback is welcome any time.
