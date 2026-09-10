import httpx
import pytest
from rimgovernor.wiki import wiki_lookup, plain


def transport(payload):
    return httpx.MockTransport(lambda request: httpx.Response(200,json=payload))


@pytest.mark.asyncio
async def test_section_budget_and_provenance():
    result = await wiki_lookup('read','Rooms','2',transport=transport({'parse':{
        'title':'Rooms','revid':123,'text':'<p>'+'x'*6100+'</p>'}}))
    assert len(result['text']) == 6000 and result['truncated']
    assert result['revision']==123 and result['url']=='https://rimworldwiki.com/wiki/Rooms'
    assert result['retrieved_at']


@pytest.mark.asyncio
async def test_contents_and_search_are_bounded():
    result=await wiki_lookup('read','Rooms',transport=transport({'parse':{
        'title':'Rooms','revid':1,'sections':[{'index':str(i),'line':'<b>Section</b>'} for i in range(1,65)]}}))
    assert len(result['sections'])==61 and result['omitted_sections']==4
    assert result['sections'][0]['id']=='0'
    result=await wiki_lookup('search','soil',transport=transport({'query':{'search':[
        {'title':'Soil','snippet':'<span>Fertility</span>'}]*4}}))
    assert len(result['matches'])==3 and result['matches'][0]['snippet']=='Fertility'


@pytest.mark.asyncio
async def test_api_error_is_not_empty_success():
    with pytest.raises(ValueError,match='could not fulfill'):
        await wiki_lookup('read','missing',transport=transport({'error':{'code':'missingtitle'}}))


@pytest.mark.asyncio
async def test_no_redirects_to_other_hosts():
    requests=[]
    def reply(request):
        requests.append(request)
        return httpx.Response(302,headers={'Location':'http://localhost/private'})
    with pytest.raises(httpx.HTTPStatusError):
        await wiki_lookup('search','soil',transport=httpx.MockTransport(reply))
    assert len(requests)==1 and requests[0].url.host=='rimworldwiki.com'


def test_plain_text_removes_scripts_and_markup():
    assert plain('<script>bad()</script><p>A &amp; B</p>')=='A & B'
