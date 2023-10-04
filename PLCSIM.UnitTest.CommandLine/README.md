# <img src="Resources/PLCSIM.CmdRunner.png" alt="PLCSIM.CmdRunner Icon" width="48" height="48" align="top"> PLCSIM.UnitTest.CommandLine

## Description

Command line runner for unit tests on PLCSIM Advanced.
Unit test results are being stored in a jUnit compliant XML file.

Program is based on plugin system that includes plugins for
- PLCSIM Advanced
- TIA Openess

> [!IMPORTANT]
> To run in CI/CD pripeline or in any other unsupervised environment, access in TIA openess firewall must be granted by an administrator.
> Can be done by manually choosing option to allow access during first run or by commandline option `whitelist`.

## System requirements

- .NET Framework 4.8
- PLCSIM Advanced plugin(s)
- TIA Openess plugin(s)

## Commandline options

Application uses Command Line Parser Library for parsing options 
(see https://github.com/commandlineparser/commandline for details on commandline options)

### Display help on command line options
```
PLCSIM.UnitTest.CommandLine.exe --help 
```

### Run unit tests
```
PLCSIM.UnitTest.CommandLine.exe run -v <tiaversion> -f <projectfile> -p <plcname> -o <outputfile> -t <timeout>
```
---
`-v` | `--version`
* TIA Portal version to use
* Required
* Available options
  * V16
  * V17
  * V18
> [!NOTE]
> Listing plugins displays the command line option for each plugin`

---
`-f` | `--file`
* Project file path (*.apXX) | Project archive file path (*.zapXX)
* Required
* Includes check for correct file extension depending on TIA Portal version that is being used
---
`-p` | `--plc`
* Name of the PLC in the TIA project
* Required
---
`-o` | `--output`
* Test result output file path
* Required
* Will automatically overwrite if exists
* Compliant to jUnit output file format (see https://github.com/testmoapp/junitxml)
---
`-t` | `--timeout`
* Timeout for running unit tests
* Optional
  * Default: 5m
* From starting PLC until message that unit tests finished
* In standard Timespan format `[-][d'.']hh':'mm':'ss['.'fffffff]`
---
`--verbose`
* Verbose logging output
* Optional
---
`--help`
* Display help on command

### List available plugins
```
PLCSIM.UnitTest.CommandLine.exe plugins 
```
Displays
- Name
- Description
- Version
- Command line option
---
`--verbose`
* Verbose logging output
* Optional
---
`--help`
* Display help on command

### Allow firewall access
> [!IMPORTANT]
> **MUST BE RUN AS ADMIN!**
```
PLCSIM.UnitTest.CommandLine.exe whitelist -v <listofversions> 
```
`-v` | `--version`
* TIA Portal versions to grant access
* Required
* Multiple version can be provided separated by space
* Available options
  * V16
  * V17
  * V18
> [!NOTE]
> Listing plugins displays the command line option for each plugin`
---
`--verbose`
* Verbose logging output
* Optional
---
`--help`
* Display help on command

## Execution sequence

1. Load PLCSIM Advanced Plugin
2. Create new PLCSIM instance
3. Power on PLCSIM instance
4. Load TIA Openess plugin
5. Open / Deflate PLC project
  - Project file name provided by commandline option	
  - Deflation into a temporary directory
6. Compile PLC (HW & SW)
  - PLC name in project provided by commandline option
7. Download PLC to PLCSIM instance
  - Enable simulation (if not set in project)
8. Start communication with PLC
9. Start PLC
10. Wait for message that unit tests are finished
  - Or timeout occurs (timeout time can be set by commandline option)
11. Save unit test results to output file
  - Output file name provided by commandline option 
12. Stop PLC
13. Stop communication with PLC
14. Power off PLCSIM instance
15. Remove PLCSIM instance
  - Cleanup PLCSIM Advanced instance directory
16. If project was deflated from archive delete temporary directory

> [!NOTE]
> Unit test run fails if any of the execution steps fails / produces an error.

## Plugins

### PLCSIM Advanced plugins
  
- API v5.0 (see project "Plugin Projects/PlcSimAdvanced.V5_0")

### TIA Openess plugins
  
- TIA Portal v16 (see project "Plugin Projects/TiaOpeness.V16")
- TIA Portal v17 (see project "Plugin Projects/TiaOpeness.V17")
- TIA Portal v18 (see project "Plugin Projects/TiaOpeness.V18")

## Implementation Details

- Plugin directory
  - see application settings
  - Debug: "\<solution directory\>/Plugins"
    - Plugin projects copy DLL to this directory on post-build trigger
  - Release: "./Plugins"
- Additional plugins can be added without recompiling main program as long as plugin contract remains the same

## CI/CD Pipeline Samples

- Gitlab [![GitLab](https://badgen.net/badge/icon/gitlab?icon=gitlab&label)](https://https://gitlab.com/)
  - see [Gitlab Pipeline](Resources/GitlabPipeline.md)
  - https://simpleicons.org/icons/jenkins.svg
- Jenkins [![Jenkins](https://img.shields.io/badge/Jenkins-blue?logo=Jenkins&logoColor=white&labelColor=gray)](https://www.jenkins.io/)
  - [!TODO] [![TODO](https://img.shields.io/badge/TODO-red?style=pastic)]([https://shields.io/](https://github.com/Lorenz-Software/PLCSIM.UnitTest))

## Authors and acknowledgment

D. Lorenz

## License

MIT
