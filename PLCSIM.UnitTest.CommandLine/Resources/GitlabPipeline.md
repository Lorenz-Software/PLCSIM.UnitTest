# Gitlab Pipeline

[![GitLab](https://badgen.net/badge/icon/gitlab?icon=gitlab&label)](https://https://gitlab.com/)

## Description

A Gitlab pipeline is executed by a Gitlab runner (see [Gitlab Runner Documentation](https://docs.gitlab.com/runner/)). The computer that is executing the pipeline needs to have the corresponding versions 
of PLCSIM Advanced and TIA Portal (incl. Openess) installed.

> [!IMPORTANT]
> Ensure to whitelist the application in TIA Openess firewall (see chapter [CommandLine Options](../README.md#Commandlineoptions)) first to avoid unidentifiable exceptions. 
> (This must be repeated every time the application is updated)

## Gitlab Runner Configuration

This is a sample of the Gitlab runner configuration.
The execution directory as well as the name of the exe file is encoded into environment variables to avoid direct references in the Gitlab pipeline.

```toml
concurrent = 1
check_interval = 0
shutdown_timeout = 0

[session_server]
  session_timeout = 1800

[[runners]]
  name = "Instance name"
  url = <ServerName>
  id = 2
  token = <Token>
  token_obtained_at = 2023-09-02T07:40:17Z
  token_expires_at = 0001-01-01T00:00:00Z
  executor = "shell"
  shell = "pwsh"
  environment = ["EXEC_DIR=C:\\Program Files\\PLCSIM.UnitTest.CommandLine", 
				 "UNITTEST_RUNNER=.\\PLCSIM.UnitTest.CommandLine.exe"]
  builds_dir = "D:\\GitlabRunner\\builds"
  cache_dir = "D:\\GitlabRunner\\cache"
  [runners.cache]
    MaxUploadedArchiveSize = 0
```

- Environment Variables
  - EXEC_DIR - Directory where application is installed
  - UNITTEST_RUNNER - Application exe

## Gitlab Pipeline

```yaml
stages:         # List of stages for jobs, and their order of execution
  - test

variables:      # Project variables
  PROJECT_FILE_V17: "$CI_PROJECT_DIR/SampleProject.zap17"       # Project file / Project archive
  PLCNAME: "PLC_1"                                              # Name of the PLC to test
  OUTPUT_FILE: "$CI_PROJECT_DIR/Tests/TestResults_V17.xml"      # Test result output file
  PARAM_V17: "run -v v17 -f '$PROJECT_FILE_V17' -p \"$PLCNAME\" -o '$OUTPUT_FILE'"  # Parameters for runner execution
  CMD: "$UNITTEST_RUNNER $PARAM_V17"        # Command line to execute ($UNITTEST_RUNNER is an environment variable defined in gitlab runner)

unit-test-job:   # This job runs in the test stage.
  stage: test    
  tags: 
    - S7 UnitTest   # Gitlab runner is configured to execute only jobs marked with this tag
  rules:
    - changes:
      - $PROJECT_FILE_V17       # Only execute in case project faile / archive has changed
      allow_failure: false
  artifacts:
    when: always
    expose_as: 'Test Result'    # Include test results as an artifact
    paths:
      - $OUTPUT_FILE
    reports:
      junit: $OUTPUT_FILE
  script:
    - echo "Running unit tests..."
    - cd $EXEC_DIR              # Environment variable defined in gitlab runner
    - echo "Executing $CMD"
    - Invoke-Expression "& $CMD"
    - if(!$?) { Exit $LASTEXITCODE }
    - echo "Checking test results..."
    - Select-Xml -Path $OUTPUT_FILE -XPath '/testsuites' | ForEach-Object { if (($_.Node.failures -gt 0) -or ($_.Node.errors -gt 0)) {exit -1} }
```