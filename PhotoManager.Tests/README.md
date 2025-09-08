# PhotoManager.Tests

Unit and integration tests for the PhotoManager application suite using NUnit framework.

## Structure

```
PhotoManager.Tests/
├── Unit/               # Unit tests for individual components
├── Integration/        # Integration tests for component interactions
└── TestData/          # Test files and sample data
```

## Running Tests

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura

# Run specific test fixture
dotnet test --filter FullyQualifiedName~DateTimeParserTests

# Run specific category
dotnet test --filter Category=Unit
```

## Test Categories

Tests are organized into categories:
- **Unit** - Fast, isolated component tests
- **Integration** - Tests that verify component interactions
- **EndToEnd** - Full workflow tests
- **Performance** - Performance and benchmark tests

## Test Coverage Goals

- Overall: ≥ 90% statement coverage
- Branch: ≥ 80% coverage
- Critical components: 100% branch coverage

## Writing Tests

### Naming Convention
Tests follow the pattern: `MethodName_Scenario_ExpectedResult`

Example:
```csharp
[Test]
public async Task ParseDateFromFileName_ValidFormats_ExtractsCorrectDate()
```

### Test Structure
Tests follow AAA pattern:
- **Arrange** - Set up test data
- **Act** - Execute the method under test
- **Assert** - Verify the results

### Test Data
- Use TestData directory for sample files
- Clean up temporary files in TearDown
- Use unique temp directories per test run

## Current Test Coverage

### Unit Tests
- ✅ DateTimeParser - Filename date extraction
- ✅ FileOrganizer - Path generation and file operations
- ⬜ ImportManager - Import orchestration logic
- ⬜ Models - Data model validation

### Integration Tests
- ⬜ End-to-end import workflow
- ⬜ Metadata extraction with real images
- ⬜ Error handling and recovery

## Dependencies
- NUnit 3.14.0
- Microsoft.NET.Test.Sdk 17.8.0
- Coverlet for code coverage