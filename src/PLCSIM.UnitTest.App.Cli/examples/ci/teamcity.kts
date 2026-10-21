// Merge this starter build type into the Kotlin DSL of a TeamCity project.
import jetbrains.buildServer.configs.kotlin.*
import jetbrains.buildServer.configs.kotlin.buildSteps.powerShell

version = "2025.07"

project {
    buildType(PlcsimUnitTest)
}

object PlcsimUnitTest : BuildType({
    name = "PLCSIM UnitTest"
    maxRunningBuilds = 1
    params {
        param("env.PLCSIM_UNITTEST_CLI", "C:\\Program Files\\PLCSIM.UnitTest\\PLCSIM.UnitTest.App.Cli.exe")
        param("env.PLCSIM_UNITTEST_CONFIG", "%teamcity.build.checkoutDir%\\ci\\plcsim-unittest.json")
        param("env.PLCSIM_UNITTEST_JUNIT", "%teamcity.build.checkoutDir%\\artifacts\\plcsim-unittest\\%teamcity.build.id%\\plcsim-unittest.xml")
        param("env.PLCSIM_UNITTEST_ANALYSIS", "%teamcity.build.checkoutDir%\\artifacts\\plcsim-unittest\\%teamcity.build.id%\\analysis")
    }
    requirements {
        equals("teamcity.agent.jvm.os.name", "Windows", "RQ_1")
        exists("system.plcsim", "RQ_2")
    }
    steps {
        powerShell {
            name = "Inspect registry and check configuration"
            scriptMode = script {
                content = """
                    & ${'$'}env:PLCSIM_UNITTEST_CLI list-profiles
                    if (${'$'}LASTEXITCODE -ne 0) { exit ${'$'}LASTEXITCODE }
                    & ${'$'}env:PLCSIM_UNITTEST_CLI check-config --file ${'$'}env:PLCSIM_UNITTEST_CONFIG
                    exit ${'$'}LASTEXITCODE
                """.trimIndent()
            }
        }
        powerShell {
            name = "Run PLC UnitTests"
            scriptMode = script {
                content = """
                    & ${'$'}env:PLCSIM_UNITTEST_CLI run --file ${'$'}env:PLCSIM_UNITTEST_CONFIG --output ${'$'}env:PLCSIM_UNITTEST_JUNIT --analysis-output ${'$'}env:PLCSIM_UNITTEST_ANALYSIS
                    ${'$'}runExitCode = ${'$'}LASTEXITCODE
                    if (Test-Path -LiteralPath ${'$'}env:PLCSIM_UNITTEST_JUNIT) {
                        Write-Host "##teamcity[importData type='junit' path='${'$'}env:PLCSIM_UNITTEST_JUNIT']"
                    }
                    exit ${'$'}runExitCode
                """.trimIndent()
            }
        }
    }
    artifactRules = """
        artifacts/plcsim-unittest/%teamcity.build.id%/plcsim-unittest.xml
        artifacts/plcsim-unittest/%teamcity.build.id%/analysis/**/*.zip => analysis
    """.trimIndent()
})
