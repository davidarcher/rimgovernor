from rimbot.projects import ProjectBook


def test_same_title_construction_actions_keep_independent_native_targets():
    book=ProjectBook()
    first=book.upsert({'title':'PlaceBuildings','source_step':'first','targets':[
        {'kind':'building','def_name':'Wall','x':10,'z':10}]})
    second=book.upsert({'title':'PlaceBuildings','source_step':'second','targets':[
        {'kind':'building','def_name':'Wall','x':20,'z':20}]})
    assert first.id!=second.id and first.targets[0].x==10 and second.targets[0].x==20
    restored=ProjectBook(book.dump())
    same=restored.upsert({'title':'New title','source_step':'first','targets':[
        {'kind':'building','def_name':'Wall','x':10,'z':10}]})
    assert same.id==first.id and len(restored.rows)==2


def test_legacy_title_refinement_cannot_overwrite_an_action_project():
    book=ProjectBook()
    action=book.upsert({'title':'PlaceBuildings','source_step':'action','targets':[
        {'kind':'building','def_name':'Wall','x':10,'z':10}]})
    legacy=book.upsert({'title':'PlaceBuildings','targets':[
        {'kind':'building','def_name':'Wall','x':20,'z':20}]})
    assert legacy.id!=action.id and action.targets[0].x==10
