using System;
using System.Collections;

namespace RavenfieldVR.Util
{
    /// <summary>
    /// A coroutine's steps run with a way out. A step that throws ends the run instead of leaving it half done: the exception goes to
    /// <c>thrown</c> (a check's report line), and <c>leftOver</c> undoes what the steps had set up (a hosted game, splitscreen on, a
    /// hand held, a seat taken). <c>leftOver</c> also runs when the iterator is disposed unfinished, but not when the steps end on
    /// their own: they put back what they set up themselves. (Unity's StopCoroutine does not dispose: a stopped check leaves its state.) A yield cannot sit in a try with a catch, so each step is taken out of
    /// the iterator inside one and yielded outside it.
    /// </summary>
    internal static class GuardedSteps
    {
        internal static IEnumerator Run(IEnumerator steps, Action<Exception> thrown, Action leftOver)
        {
            bool finished = false;
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!steps.MoveNext())
                        {
                            finished = true;
                            break;
                        }
                        current = steps.Current;
                    }
                    catch (Exception e)
                    {
                        try
                        {
                            thrown?.Invoke(e);
                        }
                        catch (Exception)
                        {
                            // (the report itself failed: the left-over below still runs)
                        }
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                if (!finished && leftOver != null)
                {
                    Core.Log.Guard("GuardedSteps: put back", leftOver);
                }
            }
        }
    }
}
