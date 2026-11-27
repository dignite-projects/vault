import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LIST_QUERY_DEBOUNCE_TIME, LocalizationService, PermissionService } from '@abp/ng.core';
import { ConfirmationService, ToasterService } from '@abp/ng.theme.shared';
import { FieldTypeResolver } from '@dignite/ng.flex-fields';
import { of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  DocumentTypeService,
  FieldDefinitionDto,
  FieldDefinitionService,
  FieldDraftSuggestionService,
  FieldPromptPolishService,
  FieldValidationRuleDto,
  SlugSuggestionService,
} from '@dignite/ng.vault-extract';
import { buildRuleForm } from '../field-validation-rules/field-validation-rules.form';
import { FieldDefinitionListComponent } from './field-definition-list.component';

// The field editor saves a field's validation rules with the field. The rules section edits the form array in place;
// what this page owns is loading a field's rules into it when the editor opens and sending them back on save.
//
// The component is constructed but never rendered: its template needs a live datatable and the flex-field
// configuration editor, while every fact here is about what the handlers put in the form and send.

const RULE: FieldValidationRuleDto = {
  description: 'The last row ends on the closing balance',
  expression: 'rows.at(-1).balance === value',
  inputs: [{ alias: 'rows', fieldId: 'f-rows' }],
};

const FIELD: FieldDefinitionDto = {
  id: 'f-closing',
  documentTypeId: 't-1',
  name: 'closingBalance',
  displayName: 'Closing balance',
  fieldTypeName: 'Number',
  configuration: {},
  displayOrder: 1,
  isRequired: false,
  isSearchable: true,
  isUniqueKey: false,
  validationRules: [RULE],
} as FieldDefinitionDto;

function setup() {
  const service = {
    getFieldTypes: vi.fn().mockReturnValue(of([])),
    getList: vi.fn().mockReturnValue(of([])),
    create: vi.fn().mockReturnValue(of(FIELD)),
    update: vi.fn().mockReturnValue(of(FIELD)),
  };

  TestBed.configureTestingModule({
    imports: [FieldDefinitionListComponent],
    providers: [
      provideRouter([]),
      { provide: LIST_QUERY_DEBOUNCE_TIME, useValue: 0 },
      { provide: PermissionService, useValue: { getGrantedPolicy: () => true } },
      { provide: LocalizationService, useValue: { instant: (key: string) => key } },
      { provide: ToasterService, useValue: { success: vi.fn(), error: vi.fn(), warn: vi.fn(), info: vi.fn() } },
      { provide: ConfirmationService, useValue: { warn: vi.fn() } },
      { provide: FieldDefinitionService, useValue: service },
      { provide: DocumentTypeService, useValue: { getVisible: () => of([]) } },
      { provide: SlugSuggestionService, useValue: { suggest: () => of({ slug: '' }) } },
      { provide: FieldDraftSuggestionService, useValue: {} },
      { provide: FieldPromptPolishService, useValue: {} },
      { provide: FieldTypeResolver, useValue: { getAll: () => [], find: () => undefined } },
    ],
  });

  const fixture = TestBed.createComponent(FieldDefinitionListComponent);
  return { component: fixture.componentInstance, service };
}

describe('FieldDefinitionListComponent — validation rules', () => {
  beforeEach(() => {
    TestBed.resetTestingModule();
  });

  it('opens a field with its rules and saves them with it', () => {
    const { component, service } = setup();

    component.openEdit(FIELD);
    expect(component.form.controls.validationRules.length).toBe(1);
    component.form.controls.validationRules.push(
      buildRuleForm({ description: 'Positive', expression: 'value > 0', inputs: [] }),
    );
    component.submit();

    expect(service.update).toHaveBeenCalledTimes(1);
    expect(service.update.mock.calls[0][1].validationRules).toEqual([
      RULE,
      { description: 'Positive', expression: 'value > 0', inputs: [] },
    ]);
  });

  it('sends an empty list once every rule is removed, so the field loses them', () => {
    // On update null would keep the field's rules; this editor shows them all, so what it sends is the whole set.
    const { component, service } = setup();

    component.openEdit(FIELD);
    component.form.controls.validationRules.removeAt(0);
    component.submit();

    expect(service.update.mock.calls[0][1].validationRules).toEqual([]);
  });

  it('starts a new field with no rules, whatever the field edited before had', () => {
    const { component, service } = setup();
    component.openEdit(FIELD);
    component.closeModal();

    component.openCreate();
    component.form.patchValue({ name: 'openingBalance', displayName: 'Opening balance', fieldTypeName: 'Number' });
    component.submit();

    expect(component.form.controls.validationRules.length).toBe(0);
    expect(service.create.mock.calls[0][0].validationRules).toEqual([]);
  });

  it('does not save while a rule is incomplete', () => {
    const { component, service } = setup();

    component.openEdit(FIELD);
    component.form.controls.validationRules.push(buildRuleForm());
    component.submit();

    expect(service.update).not.toHaveBeenCalled();
  });
});
