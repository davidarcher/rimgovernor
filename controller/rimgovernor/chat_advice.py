"""Optional chat-only advice and notebook tools; no execution authority."""
from .consultation import structured_tool as tool
from .config import ModelRole


def add_advisory_tools(rt, tools):
    schema = lambda properties, required: {'type':'object','properties':properties,'required':required,'additionalProperties':False}
    for name, field, description in (
        ('search_knowledge', 'query', 'Search advisory local strategy cards, not current facts.'),
        ('read_knowledge', 'id', 'Read an advisory strategy card returned by search.')):
        tools.append(tool(name, description, schema({field:{'type':'string','minLength':1,'maxLength':300}},[field])))
    tools.append(tool('memory', 'Read, write or delete colony-specific advisory notes. Use stable descriptive IDs to update lessons; never store action queues or assume remembered IDs remain valid.', schema({
        'operation':{'type':'string','enum':['read','write','delete']},
        'id':{'type':'string','pattern':'^[a-z0-9][a-z0-9-]{0,59}$'},
        'text':{'type':'string','maxLength':1000},
        'evidence':{'type':'string','maxLength':500}}, ['operation','id'])))
    tools.append(tool('wiki_lookup', 'Search RimWorld Wiki, read a page contents list, or read one numeric section. Prefer cached strategy cards for familiar questions; use this for missing information.', schema({'operation':{'type':'string','enum':['search','read']},'query':{'type':'string','minLength':1,'maxLength':200},'section':{'type':'string','pattern':'^[0-9]{1,4}$'}},['operation','query'])))
    auxiliary = [role.value for role in rt.router.routing.roles if role != ModelRole.STRATEGIST]
    if rt.router.enabled(ModelRole.ARCHITECT) and not rt.headless:
        from .visual_review import Region
        tools.append(tool('visual_review','Get an independent visual second opinion of the current camera view. No camera movement or orders. Verify concerns with native queries before acting.',
            schema({'question':{'type':'string','minLength':1,'maxLength':1200},
                    'focus':Region.model_json_schema()},['question'])))
    if rt.router.enabled(ModelRole.ANALYST):
        tools.append(tool('scout', 'Delegate one missing-fact investigation to the generic read-only analyst. Raw query evidence stays outside your context; returned findings are advisory.', schema(
            {'question':{'type':'string','minLength':1,'maxLength':1200},
             'sections':{'type':'array','minItems':1,'maxItems':3,'items':{'type':'string','enum':['people','resources','power','construction','space','threats']}}}, ['question','sections'])))
    if auxiliary:
        tools.append(tool('consult', 'Optionally ask one adviser a narrow question. It has no game actions, strategic authority, or recursive consultation. The same analyst handles any topic.', schema(
            {'role':{'type':'string','enum':auxiliary}, 'question':{'type':'string','minLength':1,'maxLength':1200},
             'sections':{'type':'array','minItems':1,'maxItems':3,'items':{'type':'string','enum':['people','resources','power','construction','space','threats']}},
             'include_image':{'type':'boolean'}}, ['role','question','sections'])))
