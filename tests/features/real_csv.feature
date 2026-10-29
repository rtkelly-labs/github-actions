Feature: Present real PR measurements from CSV
  File measurements are frozen from public GitHub PRs. No source content or inference is needed.

  Scenario Outline: Replay a real PR case
    Given real PR measurements from CSV case <case_id>
    When the real measurements are scored and presented
    Then all expected dimension bands match the CSV
    And character coverage matches the CSV
    And presenting the real measurements again gives identical Markdown

    Examples:
      | case_id |
      | real-01 |
      | real-02 |
      | real-03 |
      | real-04 |
      | real-05 |
      | real-06 |
      | real-07 |
      | real-08 |
      | real-09 |
      | real-10 |
      | real-11 |
      | real-12 |
