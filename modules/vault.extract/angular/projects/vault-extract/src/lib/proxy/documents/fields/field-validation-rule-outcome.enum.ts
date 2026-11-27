import { mapEnumToOptions } from '@abp/ng.core';

export enum FieldValidationRuleOutcome {
  Passed = 0,
  Failed = 1,
  Skipped = 2,
  NotEvaluated = 3,
}

export const fieldValidationRuleOutcomeOptions = mapEnumToOptions(FieldValidationRuleOutcome);
