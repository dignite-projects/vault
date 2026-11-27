import { TestBed } from '@angular/core/testing';
import { LocalizationService } from '@abp/ng.core';
import { ToasterService } from '@abp/ng.theme.shared';
import { FormArray } from '@angular/forms';
import { of, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DocumentService,
  FieldDefinitionDto,
  FieldValidationRuleDraftService,
  FieldValidationRuleDto,
  FieldValidationRuleOutcome,
} from '@dignite/ng.vault-extract';
import { FieldValidationRulesComponent } from './field-validation-rules.component';
import { RuleForm, buildRuleForm, resetRulesForm, toRuleDtos } from './field-validation-rules.form';

const BALANCE: FieldDefinitionDto = {
  id: 'f-closing',
  documentTypeId: 't-1',
  name: 'closingBalance',
  displayName: 'Closing balance',
  fieldTypeName: 'Number',
  description: 'The last balance. Check it equals the last row.',
} as FieldDefinitionDto;
const ROWS: FieldDefinitionDto = {
  id: 'f-rows',
  documentTypeId: 't-1',
  name: 'transactions',
  displayName: 'Transactions',
  fieldTypeName: 'Table',
} as FieldDefinitionDto;

const LAST_ROW: FieldValidationRuleDto = {
  description: 'The last row ends on the closing balance',
  expression: 'rows.at(-1).balance === value',
  inputs: [{ alias: 'rows', fieldId: ROWS.id! }],
};

function setup(options: { field?: FieldDefinitionDto | null; rules?: FieldValidationRuleDto[]; description?: string } = {}) {
  const draft = vi.fn().mockReturnValue(of({ items: [] }));
  const draftFromPrompt = vi.fn().mockReturnValue(of({ items: [] }));
  const test = vi.fn().mockReturnValue(of({ items: [] }));
  const getList = vi.fn().mockReturnValue(of({ totalCount: 1, items: [{ id: 'doc-1', title: 'March statement' }] }));
  const toaster = { info: vi.fn(), warn: vi.fn(), success: vi.fn(), error: vi.fn() };

  TestBed.configureTestingModule({
    imports: [FieldValidationRulesComponent],
    providers: [
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: toaster },
      { provide: FieldValidationRuleDraftService, useValue: { draft, draftFromPrompt, test } },
      { provide: DocumentService, useValue: { getList } },
    ],
  });

  const rules = new FormArray<RuleForm>([]);
  resetRulesForm(rules, options.rules ?? []);
  const field = options.field === undefined ? BALANCE : options.field;
  const fixture = TestBed.createComponent(FieldValidationRulesComponent);
  fixture.componentRef.setInput('rules', rules);
  fixture.componentRef.setInput('field', field);
  fixture.componentRef.setInput('fields', [BALANCE, ROWS]);
  fixture.componentRef.setInput('documentTypeCode', 'bank-statement');
  fixture.componentRef.setInput('description', options.description ?? field?.description ?? '');
  fixture.detectChanges();
  return { component: fixture.componentInstance, fixture, rules, draft, draftFromPrompt, test, getList, toaster };
}

describe('FieldValidationRulesComponent', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('offers the type\'s other fields as inputs, never the rule\'s own', () => {
    const { component } = setup();

    expect(component.otherFields().map(field => field.id)).toEqual([ROWS.id]);
  });

  it('adds rules by hand up to the cap of ten', () => {
    const { component, rules } = setup();

    for (let i = 0; i < 12; i++) component.addRule();

    expect(rules.length).toBe(10);
    expect(component.atLimit()).toBe(true);
  });

  it('drafts a rule from a sentence, marks it as drafted and clears the sentence', () => {
    const { component, rules, draft } = setup();
    draft.mockReturnValue(of({ items: [LAST_ROW] }));

    component.requirement.setValue('  The last row ends on the closing balance  ');
    component.draft();

    expect(draft).toHaveBeenCalledWith(
      { fieldId: BALANCE.id, description: 'The last row ends on the closing balance' },
      undefined,
    );
    expect(toRuleDtos(rules)).toEqual([LAST_ROW]);
    expect(component.isDrafted(rules.at(0))).toBe(true);
    expect(component.requirement.value).toBe('');
    expect(component.isDrafting()).toBe(false);
  });

  it('keeps the sentence and says so when the model drafts nothing', () => {
    const { component, rules, toaster } = setup();

    component.requirement.setValue('Something unclear');
    component.draft();

    expect(rules.length).toBe(0);
    expect(component.requirement.value).toBe('Something unclear');
    expect(toaster.info).toHaveBeenCalledWith('VaultExtract::FieldDefinition:ValidationRules:DraftNone', 'VaultExtract::Info');
  });

  it('does not draft for a field that is not saved yet', () => {
    // Drafting reads the field's saved definition, which a new field does not have.
    const { component, draft, getList } = setup({ field: null });

    component.requirement.setValue('The balance is positive');
    component.draft();
    component.draftFromDescription();

    expect(component.canDraft()).toBe(false);
    expect(component.canDraftFromDescription()).toBe(false);
    expect(draft).not.toHaveBeenCalled();
    expect(getList).not.toHaveBeenCalled();
  });

  it('drafts from the description only while the typed description is the saved one', () => {
    // The server reads the saved description; drafting from it while the editor shows another would draft rules
    // for checks the operator no longer sees.
    const { component, fixture, draftFromPrompt } = setup({ description: 'A new description, not saved' });

    expect(component.canDraftFromDescription()).toBe(false);
    component.draftFromDescription();
    expect(draftFromPrompt).not.toHaveBeenCalled();

    fixture.componentRef.setInput('description', BALANCE.description);
    expect(component.canDraftFromDescription()).toBe(true);
    component.draftFromDescription();
    expect(draftFromPrompt).toHaveBeenCalledWith({ fieldId: BALANCE.id }, undefined);
  });

  it('adds drafts from the description up to the cap and warns about the rest', () => {
    const existing = Array.from({ length: 8 }, (_, i) => ({ ...LAST_ROW, description: `Rule ${i}` }));
    const { component, rules, draftFromPrompt, toaster } = setup({ rules: existing });
    draftFromPrompt.mockReturnValue(of({ items: [1, 2, 3].map(i => ({ ...LAST_ROW, description: `Draft ${i}` })) }));

    component.draftFromDescription();

    expect(rules.getRawValue().map(rule => rule.description).slice(8)).toEqual(['Draft 1', 'Draft 2']);
    expect(toaster.warn).toHaveBeenCalledWith(
      'VaultExtract::FieldDefinition:ValidationRules:DraftCapped',
      'VaultExtract::Warning',
      { messageLocalizationParams: ['10'] },
    );
  });

  it('offers the type\'s newest documents to try the rules on, without an error dialog', () => {
    const { component, getList } = setup();

    expect(getList).toHaveBeenCalledWith(
      { documentTypeCode: 'bank-statement', sorting: 'creationTime desc', skipCount: 0, maxResultCount: 20 },
      { skipHandleError: true },
    );
    expect(component.documents().map(document => document.id)).toEqual(['doc-1']);
  });

  it('tries the rules as they stand and shows each outcome by position', () => {
    const second: FieldValidationRuleDto = { description: 'Positive', expression: 'value > 0', inputs: [] };
    const { component, rules, test } = setup({ rules: [LAST_ROW, second] });
    test.mockReturnValue(of({
      items: [
        { outcome: FieldValidationRuleOutcome.Failed, detail: '239337 ≠ 239373' },
        { outcome: FieldValidationRuleOutcome.Passed },
      ],
    }));
    rules.at(1).controls.expression.setValue('value >= 0');

    component.testDocumentId.setValue('doc-1');
    component.test();

    expect(test).toHaveBeenCalledWith(
      { fieldId: BALANCE.id, documentId: 'doc-1', rules: [LAST_ROW, { ...second, expression: 'value >= 0' }] },
      undefined,
    );
    expect(component.resultOf(0)).toEqual({ outcome: FieldValidationRuleOutcome.Failed, detail: '239337 ≠ 239373' });
    expect(component.resultOf(1)?.outcome).toBe(FieldValidationRuleOutcome.Passed);
  });

  it('drops the outcomes once a rule changes, since they describe the rules tried', () => {
    const { component, rules, test } = setup({ rules: [LAST_ROW] });
    test.mockReturnValue(of({ items: [{ outcome: FieldValidationRuleOutcome.Passed }] }));
    component.testDocumentId.setValue('doc-1');
    component.test();
    expect(component.resultOf(0)).not.toBeNull();

    rules.at(0).controls.expression.setValue('true');

    expect(component.resultOf(0)).toBeNull();
  });

  it('drops outcomes that come back for rules changed while the run was out', () => {
    // The outcomes are by position: after a rule is removed they would sit beside the wrong rule.
    const pending = new Subject<{ items: { outcome: FieldValidationRuleOutcome }[] }>();
    const second: FieldValidationRuleDto = { description: 'Positive', expression: 'value > 0', inputs: [] };
    const { component, rules, test } = setup({ rules: [LAST_ROW, second] });
    test.mockReturnValue(pending);
    component.testDocumentId.setValue('doc-1');
    component.test();

    rules.removeAt(0);
    pending.next({ items: [{ outcome: FieldValidationRuleOutcome.Failed }, { outcome: FieldValidationRuleOutcome.Passed }] });

    expect(component.resultOf(0)).toBeNull();
    expect(component.isTesting()).toBe(false);
  });

  it('does not try rules that are invalid or without a document', () => {
    const { component, rules, test } = setup({ rules: [LAST_ROW] });

    component.test();
    expect(test).not.toHaveBeenCalled();

    component.testDocumentId.setValue('doc-1');
    rules.at(0).controls.expression.setValue('');
    component.test();
    expect(test).not.toHaveBeenCalled();
  });

  it('cancels a draft still in flight when the editor closes', () => {
    // The section lives inside the modal; a late draft must not land in the next field the operator opens.
    const pending = new Subject<{ items: FieldValidationRuleDto[] }>();
    const { component, fixture, rules, draft } = setup();
    draft.mockReturnValue(pending);
    component.requirement.setValue('The balance is positive');
    component.draft();

    fixture.destroy();
    pending.next({ items: [LAST_ROW] });

    expect(rules.length).toBe(0);
  });
});

describe('validation rule form', () => {
  it('refuses value as an alias, a repeated alias and a field read twice', () => {
    const rule = buildRuleForm({
      description: 'd',
      expression: 'a + b',
      inputs: [{ alias: 'value', fieldId: 'f-1' }],
    });
    expect(rule.controls.inputs.at(0).controls.alias.hasError('valueAlias')).toBe(true);

    const twice = buildRuleForm({
      description: 'd',
      expression: 'a + b',
      inputs: [{ alias: 'a', fieldId: 'f-1' }, { alias: 'a', fieldId: 'f-2' }],
    });
    expect(twice.controls.inputs.hasError('duplicateInput')).toBe(true);

    const sameField = buildRuleForm({
      description: 'd',
      expression: 'a + b',
      inputs: [{ alias: 'a', fieldId: 'f-1' }, { alias: 'b', fieldId: 'f-1' }],
    });
    expect(sameField.controls.inputs.hasError('duplicateInput')).toBe(true);
  });

  it('refuses an alias that is not an identifier and a description on two lines', () => {
    const rule = buildRuleForm({
      description: 'first\nsecond',
      expression: 'true',
      inputs: [{ alias: '1rows', fieldId: 'f-1' }],
    });

    expect(rule.controls.description.hasError('oneLine')).toBe(true);
    expect(rule.controls.inputs.at(0).controls.alias.hasError('pattern')).toBe(true);
  });

  it('refuses a description or an expression of whitespace alone, as the server does', () => {
    const rule = buildRuleForm({ description: '   ', expression: ' \n ', inputs: [] });

    expect(rule.controls.description.hasError('required')).toBe(true);
    expect(rule.controls.expression.hasError('required')).toBe(true);
  });

  it('round-trips the rules it was given', () => {
    const rules = new FormArray<RuleForm>([]);
    resetRulesForm(rules, [LAST_ROW]);

    expect(toRuleDtos(rules)).toEqual([LAST_ROW]);

    resetRulesForm(rules);
    expect(rules.length).toBe(0);
  });
});
