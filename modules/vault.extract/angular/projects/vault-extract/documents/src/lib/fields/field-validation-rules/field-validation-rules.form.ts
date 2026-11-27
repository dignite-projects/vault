import {
  AbstractControl,
  FormArray,
  FormControl,
  FormGroup,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { FieldValidationRuleDto } from '@dignite/ng.vault-extract';

// Mirrors FieldValidationRuleConsts (Domain.Shared). The server checks every one of these again when the field is
// saved or a rule is tried; the client copies only enable the buttons and mark a control before the round trip.
export const MAX_RULES_PER_FIELD = 10;
export const MAX_RULE_DESCRIPTION_LENGTH = 256;
export const MAX_RULE_EXPRESSION_LENGTH = 4000;
export const MAX_INPUTS_PER_RULE = 10;
export const VALUE_ALIAS = 'value';
const ALIAS_PATTERN = /^[A-Za-z_][A-Za-z0-9_]{0,63}$/;

export type RuleInputForm = FormGroup<{
  alias: FormControl<string>;
  fieldId: FormControl<string>;
}>;

export type RuleForm = FormGroup<{
  description: FormControl<string>;
  expression: FormControl<string>;
  inputs: FormArray<RuleInputForm>;
}>;

export type RulesFormArray = FormArray<RuleForm>;

/** The rule's description is shown on one line when it fails, so a line break is refused, as the server does. */
const oneLine = (control: AbstractControl<string>): ValidationErrors | null =>
  /[\r\n]/.test(control.value ?? '') ? { oneLine: true } : null;

/** Required, and not only whitespace, which the server's [Required] refuses as well. */
const notBlank = (control: AbstractControl<string>): ValidationErrors | null =>
  (control.value ?? '').trim() ? null : { required: true };

const notValueAlias =(control: AbstractControl<string>): ValidationErrors | null =>
  control.value === VALUE_ALIAS ? { valueAlias: true } : null;

/** Within one rule an alias names one input and a field is read once, as the server's input check requires. */
const distinctInputs = (control: AbstractControl): ValidationErrors | null => {
  const inputs = (control as FormArray<RuleInputForm>).controls.map(input => input.getRawValue());
  const aliases = inputs.map(input => input.alias).filter(alias => alias);
  const fieldIds = inputs.map(input => input.fieldId).filter(fieldId => fieldId);
  return new Set(aliases).size < aliases.length || new Set(fieldIds).size < fieldIds.length
    ? { duplicateInput: true }
    : null;
};

export function buildRuleInputForm(alias = '', fieldId = ''): RuleInputForm {
  return new FormGroup({
    alias: new FormControl(alias, {
      nonNullable: true,
      validators: [Validators.required, Validators.pattern(ALIAS_PATTERN), notValueAlias],
    }),
    fieldId: new FormControl(fieldId, { nonNullable: true, validators: [Validators.required] }),
  });
}

export function buildRuleForm(rule?: FieldValidationRuleDto): RuleForm {
  return new FormGroup({
    description: new FormControl(rule?.description ?? '', {
      nonNullable: true,
      validators: [notBlank, Validators.maxLength(MAX_RULE_DESCRIPTION_LENGTH), oneLine],
    }),
    expression: new FormControl(rule?.expression ?? '', {
      nonNullable: true,
      validators: [notBlank, Validators.maxLength(MAX_RULE_EXPRESSION_LENGTH)],
    }),
    inputs: new FormArray(
      (rule?.inputs ?? []).map(input => buildRuleInputForm(input.alias, input.fieldId)),
      { validators: [distinctInputs] },
    ),
  });
}

/** Replaces the array's rules with the given ones, as the modal does each time it opens. */
export function resetRulesForm(rules: RulesFormArray, values: readonly FieldValidationRuleDto[] = []): void {
  rules.clear({ emitEvent: false });
  for (const rule of values) {
    rules.push(buildRuleForm(rule), { emitEvent: false });
  }
  rules.updateValueAndValidity();
}

/** The rules as the field API and the dry run take them. */
export function toRuleDtos(rules: RulesFormArray): FieldValidationRuleDto[] {
  return rules.getRawValue().map(rule => ({
    description: rule.description.trim(),
    expression: rule.expression,
    inputs: rule.inputs.map(input => ({ alias: input.alias.trim(), fieldId: input.fieldId })),
  }));
}
