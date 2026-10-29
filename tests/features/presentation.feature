Feature: Deterministic PR presentation
  Presentation consumes frozen evidence and supplied agent judgments without network access.

  Scenario: A complete small PR with a valid agent judgment
    Given a frozen PR with one small source file
    And an agent recommends keeping the PR together
    When the assessment is presented
    Then the report has error code 0
    And the report shows size XS
    And the report explains the gh stack decision

  Scenario: An agent has not supplied a judgment
    Given a frozen PR with one small source file
    When the assessment is presented
    Then the report has error code 2
    And the report requires review without inferring a split

  Scenario: Many short files increase review volume
    Given a frozen PR with eight one-line source files
    When the assessment is presented
    Then the report shows size L

  Scenario: Minified source increases review volume
    Given a frozen PR with one 50000-character source line
    When the assessment is presented
    Then the report shows size XXL

  Scenario: Missing patch evidence stays incomplete
    Given a frozen PR with an omitted textual patch
    And an agent recommends keeping the PR together
    When the assessment is presented
    Then the report has error code 2
    And the report describes its size as a lower bound
